﻿#region Copyright
/*
This file came from Managed Media Aggregation, You can always find the latest version @ https://github.com/juliusfriedman/net7mma_core
  
 Julius.Friedman@gmail.com / (SR. Software Engineer ASTI Transportation Inc. https://www.asti-trans.com)

Permission is hereby granted, free of charge, 
 * to any person obtaining a copy of this software and associated documentation files (the "Software"), 
 * to deal in the Software without restriction, 
 * including without limitation the rights to :
 * use, 
 * copy, 
 * modify, 
 * merge, 
 * publish, 
 * distribute, 
 * sublicense, 
 * and/or sell copies of the Software, 
 * and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
 * 
 * 
 * JuliusFriedman@gmail.com should be contacted for further details.

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. 
 * 
 * IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, 
 * DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, 
 * TORT OR OTHERWISE, 
 * ARISING FROM, 
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 * 
 * v//
 */
#endregion

using Media.Common;
using Media.Common.Extensions.Socket;
using Media.Rtp;
using Media.Sdp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Media.Rtsp;

/// <summary>
/// Implements RFC 2326
/// https://www.ietf.org/rfc/rfc2326.txt
/// Provides facilities for communication with an RtspServer to establish one or more Rtp Transport Channels.
/// </summary>
public class RtspClient : Common.SuppressedFinalizerDisposable, Media.Common.ISocketReference
{
    //Todo use SocketConfiguration
    /// <summary>
    /// Handle the configuration required for the given socket
    /// </summary>
    /// <param name="socket"></param>
    internal static void ConfigureRtspSocket(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);

        //Xamarin's .net implementation on Android suffers from nuances that neither core nor mono suffer from.
        //If this option fails to be set there the socket can't be used easily.
        if (Media.Common.Extensions.RuntimeExtensions.IsAndroid is false)
        {
            //Ensure the address can be re-used
            Media.Common.Extensions.Exception.ExceptionExtensions.ResumeOnError(() =>
                Media.Common.Extensions.Socket.SocketExtensions.EnableAddressReuse(socket));

            //Windows >= 10 and Some Unix
            Media.Common.Extensions.Exception.ExceptionExtensions.ResumeOnError(() =>
                Media.Common.Extensions.Socket.SocketExtensions.EnableUnicastPortReuse(socket));
        }

        //It was reported that Mono on iOS has a bug with SendBufferSize, ReceiveBufferSize and by looking further possibly SetSocketOption in general...
        //Mono goes through too much trouble to verify socket options and should probably just pass them along to the native layer.
        //SendBufferSize,ReceiveBufferSize and SetSocketOption is supposedly fixed in the latest versions but still do too much option verification...

        //Don't buffer send.
        Common.Extensions.Exception.ExceptionExtensions.ResumeOnError(() =>
            socket.SendBufferSize = 0);

        //Don't buffer receive.
        Common.Extensions.Exception.ExceptionExtensions.ResumeOnError(() =>
            socket.ReceiveBufferSize = 0);

        //Dont fragment for ip4, ip6 does ptmud
        if (socket.AddressFamily == AddressFamily.InterNetwork)
            Common.Extensions.Exception.ExceptionExtensions.ResumeOnError(() =>
                socket.DontFragment = true);

        //Rtsp over Tcp
        if (socket.ProtocolType == ProtocolType.Tcp)
        {
            // Set option that allows socket to close gracefully without lingering.
            Common.Extensions.Exception.ExceptionExtensions.ResumeOnError(() =>
                Media.Common.Extensions.Socket.SocketExtensions.DisableLinger(socket));

            //Allow more than one byte of urgent data, maybe not supported on the stack.
            Common.Extensions.Exception.ExceptionExtensions.ResumeOnError(() =>
                Media.Common.Extensions.Socket.SocketExtensions.EnableTcpExpedited(socket));

            //Receive any out of band data in the normal data stream, maybe not supported on the stack
            Common.Extensions.Exception.ExceptionExtensions.ResumeOnError(() =>
                Media.Common.Extensions.Socket.SocketExtensions.EnableTcpOutOfBandDataInLine(socket));

            //If both send and receieve buffer size are 0 then there is no coalescing when nagle's algorithm is disabled
            Common.Extensions.Exception.ExceptionExtensions.ResumeOnError(() =>
                Media.Common.Extensions.Socket.SocketExtensions.DisableTcpNagelAlgorithm(socket));

            //Handle options which are known to be different per Operating System
            if (Common.Extensions.OperatingSystemExtensions.IsWindows)
            {
                //Retransmit for 0 sec.
                Common.Extensions.Exception.ExceptionExtensions.ResumeOnError(() =>
                    Media.Common.Extensions.Socket.SocketExtensions.DisableTcpRetransmissions(socket));

                // Enable No Syn Retries
                Common.Extensions.Exception.ExceptionExtensions.ResumeOnError(() =>
                    Media.Common.Extensions.Socket.SocketExtensions.EnableTcpNoSynRetries(socket));

                // Set OffloadPreferred
                Common.Extensions.Exception.ExceptionExtensions.ResumeOnError(() =>
                    Media.Common.Extensions.Socket.SocketExtensions.SetTcpOffloadPreference(socket));

                //Done in ProcessEndConnect based on ConnectionTime
                //Enable Congestion Algorithm (when there is not enough bandwidth this sometimes helps)
                //Common.Extensions.Exception.ExceptionExtensions.ResumeOnError(() =>
                //    Media.Common.Extensions.Socket.SocketExtensions.EnableTcpCongestionAlgorithm(socket));

                // For network debugging
                //Common.Extensions.Exception.ExceptionExtensions.ResumeOnError(() =>
                //    Media.Common.Extensions.Socket.SocketExtensions.EnableTcpTimestamp(socket));                   
            }
        }
    }

    public const int DefaultBufferSize = RtspMessage.MaximumLength * 2;

    public const double DefaultProtocolVersion = 1.0;

    public static readonly TimeSpan DefaultConnectionTime = TimeSpan.FromMilliseconds(500);

    public static readonly TimeSpan DefaultSessionTimeout = TimeSpan.FromSeconds(60);

    public const string DefaultDateFormat = "r";

    #region Nested Types

    //Needs to have seperation or be eliminated, currently only alows for a certain style to be forced on a Uri which is otherwise, e.g. rtsp over http, tcp over udp etc.

    /// <summary>
    /// Specefies the underlying protocols required to communicate with the remote end point.
    /// </summary>
    public enum ClientProtocolType
    {
        Tcp = ProtocolType.Tcp,
        Reliable = Tcp,
        Udp = ProtocolType.Udp,
        Unreliable = Udp,
        Http = 2,
        Secure = 4
    }

    #endregion

    #region Fields

    #region Internal Read Only

    /// <summary>
    /// Represents a value which uniquely identifies this instance.
    /// </summary>
    internal readonly Guid InternalId = Guid.NewGuid();

    /// <summary>
    /// A threading resource which is used to synchronize access to the underlying buffer during message parsing and completion.
    /// </summary>
    internal readonly ManualResetEventSlim m_InterleaveEvent;

    /// <summary>
    /// The media items which are in the play state.
    /// </summary>
    internal readonly Dictionary<MediaDescription, MediaSessionState> m_Playing = [];//Could just be a list but the dictionary offers faster indexing at the cost of more memory...

    //Runtime only, should be moved to common.

    public enum MediaStatus : short
    {
        //<- User defined.
        Unknown = 0,
        Buffering = 1,
        Playing = 2,
        Paused = 4,
        Stopped = 8,
        Seeking = 16,
        Encrypting = 32,
        Decrypting = 64,
        Reserved = 128,
        Error = 255
        //-> User defined.
    }

    /// <summary>
    /// A class which can assist with the determination of the state of a media which has been described in a session.
    /// </summary>
    public class MediaSessionState
    {
        private MediaStatus m_MediaStatus;

        /// <summary>
        /// Not used by the library.
        /// </summary>
        public object Reserved;
        private readonly SessionDescription m_SessionDescription;
        private readonly MediaDescription m_MediaDescription;

        public DateTime MediaStartedUtc { get; protected internal set; }

        public DateTime LastStatusChangeUtc { get; protected internal set; }

        public TimeSpan? MediaDuration { get; protected internal set; }

        //public TimeSpan NonPlayTime 

        public MediaStatus Status
        {
            get { return m_MediaStatus; }
            set
            {
                if (m_MediaStatus == value) return;

                m_MediaStatus = value;

                LastStatusChangeUtc = DateTime.UtcNow;

                if (MediaStartedUtc == DateTime.MinValue && value is MediaStatus.Playing)
                {
                    MediaStartedUtc = LastStatusChangeUtc;
                }

                //Could keep a seperate time span which would be used to keep track of non play time..
                //e.g. if value is not playing then cululate to non play time.

            }
        }

        /// <summary>
        /// Total time since Status was changed.
        /// </summary>
        public TimeSpan SinceStateChange { get { return DateTime.UtcNow - LastStatusChangeUtc; } }

        /// <summary>
        /// Total time since Started
        /// </summary>
        public TimeSpan TotalPlayTime { get { return DateTime.UtcNow - MediaStartedUtc; } }

        //neg value indicates playing over the expected time..
        public TimeSpan RemainingPlayTime
        {
            get
            {
                TimeSpan result = TimeSpan.Zero;

                //If the media duration has a value when use that value
                if (MediaDuration.HasValue)
                {
                    result = MediaDuration.Value;

                }
                else //Determine the MediaDuraction.
                {
                    SessionDescriptionLine rangeLine = m_MediaDescription.RangeLine ?? (Common.IDisposedExtensions.IsNullOrDisposed(m_SessionDescription) is false ? m_SessionDescription.RangeLine : null);

                    //If there is no RangeLine then use the TimeDescription
                    if (rangeLine is null)
                    {
                        TimeDescription td = m_MediaDescription.GetTimeDescription(m_SessionDescription);

                        if (td is null) return TimeSpan.MaxValue;

                        if (td.IsPermanent || td.TimeDescriptionLine.Unbounded) return Common.Extensions.TimeSpan.TimeSpanExtensions.InfiniteTimeSpan;

                        result = (td.NtpStopDateTime - td.NtpStartDateTime) - TotalPlayTime;

                        if (result <= TimeSpan.Zero && td.HasRepeatTimes)
                        {
                            //Todo, Check repeat times...

                            //extension -> IsValidRepeatTime(DateTime)
                        }

                    }
                    else if (rangeLine.m_Parts.Count > 0)
                    {

                        if (Media.Sdp.SessionDescription.TryParseRange(rangeLine.m_Parts.First(), out string type, out result, out result))
                        {
                            MediaDuration = result;
                        }
                    }

                }

                //Account for time in state if not playing, might need non play time which would be calulcated through status changes.
                result = result.Subtract(SinceStateChange);

                return result;
            }
        }


        public MediaSessionState(SessionDescription sdp, MediaDescription desc)
        {
            if (sdp is null) throw new ArgumentNullException(nameof(sdp));
            if (desc is null) throw new ArgumentNullException(nameof(desc));

            //Must be a media description in the sdp such that any time information may propagate.
            if (sdp.MediaDescriptions.Contains(desc) is false) throw new InvalidOperationException();

            m_SessionDescription = sdp;

            m_MediaDescription = desc;
        }

    }

    //Todo, enum for session per media etc..?

    //Really needs to be Connection or session will also need to refer to a connection
    internal readonly ConcurrentDictionary<string, RtspSession> m_Sessions = new();

    #endregion

    #region Private

    internal ClientProtocolType m_RtspProtocol;

    internal RtspMessage m_LastTransmitted;

    internal AuthenticationSchemes m_AuthenticationScheme;

    //Todo should also store time of last auth... 
    internal string m_AuthorizationHeader; //could also just store the parts or use a StringCollection

    //Todo
    //readonly List<Uri> m_History = new List<Uri>();

    /// <summary>
    /// The current location the media
    /// </summary>
    private Uri m_InitialLocation, m_PreviousLocation, m_CurrentLocation;

    private List<IPAddress> m_RemoteHostAddresses = new List<IPAddress>();

    /// <summary>
    /// The buffer this client uses for all requests 4MB * 2 by default.
    /// </summary>
    internal Common.MemorySegment m_Buffer;

    /// <summary>
    /// The remote IPAddress to which the Location resolves via Dns
    /// </summary>
    internal IPAddress m_RemoteIP;

    /// <summary>
    /// The remote RtspEndPoint
    /// </summary>
    internal EndPoint m_RemoteRtsp;

    /// <summary>
    /// The socket used for Rtsp Communication
    /// </summary>
    internal Socket m_RtspSocket;

    /// <summary>
    /// The protcol in which Rtsp data will be transpored from the server
    /// </summary>
    internal ProtocolType m_RtpProtocol;

    /// <summary>
    /// The session description associated with the media at Location
    /// </summary>
    internal SessionDescription m_SessionDescription;

    /// <summary>
    /// Keep track of timed values.
    /// </summary>
    internal TimeSpan m_RtspSessionTimeout = DefaultSessionTimeout,
        m_ConnectionTime = Media.Common.Extensions.TimeSpan.TimeSpanExtensions.InfiniteTimeSpan,
        m_LastServerDelay = Media.Common.Extensions.TimeSpan.TimeSpanExtensions.InfiniteTimeSpan,
        //Appendix G.  Requirements for Unreliable Transport of RTSP
        m_LastMessageRoundTripTime = DefaultConnectionTime;

    /// <summary>
    /// Keep track of certain values.
    /// </summary>
    internal int m_SentBytes, m_ReceivedBytes,
         m_RtspPort,
         m_CSeq = -1, m_RCSeq, //-1 values, rtsp 2. indicates to start at 0...
         m_SentMessages, m_ReTransmits,
         m_ReceivedMessages,
         m_PushedMessages,
         m_MaximumTransactionAttempts = (int)Media.Common.Extensions.TimeSpan.TimeSpanExtensions.MicrosecondsPerMillisecond,//10
         m_SocketPollMicroseconds;

    //Todo, Two timers? should use a single thread instead....
    private Timer m_KeepAliveTimer, m_ProtocolMonitor;

    internal DateTime? m_BeginConnect, m_EndConnect, m_StartedPlaying;

    //Todo,
    //List<Sdp.MediaDescription> For playing and paused. Could use a List<Tuple<TimeSpan, MediaDescription>>> to allow the timeline when pausing etc..

    internal NetworkCredential m_Credential;

    #endregion

    #region Internal Private

    internal string m_UserAgent = "ASTI RTSP Client", m_SessionId = string.Empty;//, m_TransportMode;

    //Should not be tied to RTP, needs to be another interface which has Activate, etc RtspTransport might be a possible design, would required all Transports to be registered

    internal RtpClient m_RtpClient;

    #endregion

    #region Public

    /// <summary>
    /// As given by the OPTIONS response or set otherwise.
    /// </summary>
    public readonly HashSet<string> SupportedFeatures = [];

    /// <summary>
    /// Values which will be set in the Required tag.
    /// </summary>
    public readonly HashSet<string> RequiredFeatures = [];

    /// <summary>
    /// Any additional headers which may be required by the RtspClient.
    /// </summary>
    public readonly Dictionary<string, string> AdditionalHeaders = [];

    /// <summary>
    /// Gets the methods supported by the server received in the options request.
    /// </summary>
    public readonly HashSet<string> SupportedMethods = [];

    //Todo, should be property with protected set.

    /// <summary>
    /// A ILogging instance
    /// </summary>
    public Common.ILogging Logger;

    /// <summary>
    /// The value passed to the <see cref="DateTime.ToString"/> method when <see cref="DateRequests"/> is true.
    /// </summary>
    public string DateFormat = DefaultDateFormat;

    #endregion

    #endregion

    #region Properties

    #region Automatically Implemented

    /// <summary>
    /// Gets or sets a value indicating of the RtspSocket should be left open when Disposing.
    /// </summary>
    public bool LeaveOpen
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        set;
    }

    /// <summary>
    /// The version of Rtsp the client will utilize in messages
    /// </summary>
    public double ProtocolVersion
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        set;
    }

    /// <summary>
    /// Indicates if the <see cref="StartedPlaying"/> property will be set as a result of handling the Play event.
    /// </summary>
    public bool HandlePlayEvent
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        set;
    }

    /// <summary>
    /// Indicates if the <see cref="StartedPlaying"/> will not have a value as a result of handling the Stop event.
    /// </summary>
    public bool HandleStopEvent
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        set;
    }

    /// <summary>
    /// Allows the order of media to be determined when <see cref="StartPlaying"/>  is called
    /// </summary>
    public Action<IEnumerable<MediaDescription>> SetupOrder
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        set;
    }

    //Todo make SocketConfiguration

    /// <summary>
    /// Gets or Sets the method which is called when the <see cref="RtspSocket"/> is created, 
    /// typically during the call to <see cref="Connect"/>
    /// By default <see cref="ConfigureRtspSocket"/> is utilized.
    /// </summary>
    public Action<Socket> ConfigureSocket
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        set;
    }

    /// <summary>
    /// Indicates if the client will try to automatically reconnect during send or receive operations.
    /// </summary>
    public bool AutomaticallyReconnect
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        set;
    }

    /// <summary>
    /// Indicates if the client will automatically disconnect the RtspSocket after StartPlaying is called.
    /// </summary>
    public bool AutomaticallyDisconnectAfterStartPlaying
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        set;
    }

    /// <summary>
    /// Indicates if the client will send a <see cref="KeepAliveRequest"/> during <see cref="StartPlaying"/> if no data is flowing immediately after the PLAY response is received.
    /// </summary>
    public bool SendKeepAliveImmediatelyAfterStartPlaying
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        set;
    }

    /// <summary>
    /// Indicates if the client will add the Timestamp header to outgoing requests.
    /// </summary>
    public bool TimestampRequests
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        set;
    }

    /// <summary>
    /// Indicates if the client will use the Timestamp header to incoming responses.
    /// </summary>
    public bool CalculateServerDelay
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        set;
    }

    /// <summary>
    /// Indicates if the client will send the Blocksize header during the SETUP request.
    /// The value of which will reflect the <see cref="Buffer.Count"/>
    /// </summary>
    public bool SendBlocksize
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        set;
    }

    /// <summary>
    /// Indicates if the Date header should be sent during requests.
    /// </summary>
    public bool DateRequests
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        set;
    }

    /// <summary>
    /// Indicates if the RtspClient will send the UserAgent header.
    /// </summary>
    public bool SendUserAgent
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        set;
    }

    //Maybe AllowHostChange
    //public bool IgnoreRedirectOrFound { get; set; }

    /// <summary>
    /// Indicates if the client will take any `X-` headers and use them in future requests.
    /// </summary>
    public bool EchoXHeaders
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        set;
    }

    /// <summary>
    /// Indicates if the client will process messages which are pushed during the session.
    /// </summary>
    public bool IgnoreServerSentMessages
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        set;
    }

    /// <summary>
    /// Indicates if Keep Alive Requests will be sent
    /// </summary>
    public bool DisableKeepAliveRequest
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        set;
    }

    /// <summary>
    /// Gets or Sets a value which indicates if the client will attempt an alternate style of connection if one cannot be established successfully.
    /// Usually only useful under UDP when NAT prevents RTP packets from reaching a client, it will then attempt TCP or HTTP transport.
    /// </summary>
    public bool AllowAlternateTransport
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        set;
    }

    #endregion

    #region Computed Properties

    /// <summary>
    /// Gets or sets the maximum amount of microseconds the <see cref="RtspSocket"/> will wait before performing an operations.
    /// </summary>
    public int SocketPollMicroseconds
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_SocketPollMicroseconds; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        set { m_SocketPollMicroseconds = value; }
    }

    /// <summary>
    /// Gets the remote <see cref="EndPoint"/>
    /// </summary>
    public EndPoint RemoteEndpoint
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_RemoteRtsp; }
    }

    /// <summary>
    /// Indicates if the RtspClient is currently sending or receiving data.
    /// </summary>
    public bool InUse
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get
        {
            return m_InterleaveEvent.IsSet is false &&
                m_InterleaveEvent.Wait(Common.Extensions.TimeSpan.TimeSpanExtensions.TwoHundedNanoseconds) is false; //m_InterleaveEvent.Wait(1); // ConnectionTime
        }
    }

    /// <summary>
    /// Gets or Sets the socket used for communication
    /// </summary>
    public Socket RtspSocket
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_RtspSocket; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        protected internal set
        {

            bool wasDisconnected = IsConnected is false;

            m_RtspSocket = value;

            //Ensure not connected if the socket is removed
            if (m_RtspSocket is null)
            {
                m_BeginConnect = m_EndConnect = null;

                m_ConnectionTime = Media.Common.Extensions.TimeSpan.TimeSpanExtensions.InfiniteTimeSpan;

                return;
            }

            //If the socket is connected
            if (m_RtspSocket.Connected)
            {
                m_SocketPollMicroseconds = (int)Media.Common.Extensions.NetworkInterface.NetworkInterfaceExtensions.GetInterframeGapMicroseconds(Media.Common.Extensions.NetworkInterface.NetworkInterfaceExtensions.GetNetworkInterface(m_RtspSocket));

                m_SocketPollMicroseconds /= 10;

                Media.Common.Binary.Min(m_SocketPollMicroseconds, ResponseTimeoutInterval);

                //If tcp...
                if (m_RtspSocket.ProtocolType == ProtocolType.Tcp)
                {
                    //Do not re-transmit
                    Media.Common.Extensions.Exception.ExceptionExtensions.ResumeOnError(() => Media.Common.Extensions.Socket.SocketExtensions.DisableTcpRetransmissions(m_RtspSocket));

                    // Enable No Syn Retries
                    Media.Common.Extensions.Exception.ExceptionExtensions.ResumeOnError(() => Media.Common.Extensions.Socket.SocketExtensions.EnableTcpNoSynRetries(m_RtspSocket));

                    // Set OffloadPreferred
                    Media.Common.Extensions.Exception.ExceptionExtensions.ResumeOnError(() => Media.Common.Extensions.Socket.SocketExtensions.SetTcpOffloadPreference(m_RtspSocket));
                }

                //SO_CONNECT_TIME only exists on Windows...
                //There are options if the stack supports it elsewhere.

                //Set default values to indicate connected

                m_BeginConnect = m_EndConnect = DateTime.UtcNow;

                m_ConnectionTime = TimeSpan.Zero;

                //Use the remote information from the existing socket rather than the location.

                m_RemoteRtsp = m_RtspSocket.RemoteEndPoint;

                if (m_RemoteRtsp is IPEndPoint remote)
                {
                    m_RemoteIP = remote.Address;
                }

                //If wasDisconnected and now conneted raise an vent 
                if (wasDisconnected)
                {
                    OnConnected();
                }

            }
            else if (wasDisconnected is false) //The new socket is not connected, if was previously CONNECTED
            {
                //Raise the Disconnected event.
                OnDisconnected();
            }
        }
    }

    /// <summary>
    /// Gets or Sets the buffer used for data reception
    /// </summary>
    protected internal Common.MemorySegment Buffer
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_Buffer; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        set { m_Buffer = value; }
    }

    /// <summary>
    /// Indicates if the RtspClient shares the <see cref="RtspSocket"/> with the underlying Transport.
    /// </summary>
    public bool SharesSocket
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get
        {
            //The socket is shared with the GC
            if (Common.IDisposedExtensions.IsNullOrDisposed(this)) return true;

            // A null or disposed client or one which is no longer connected cannot share the socket
            if (Common.IDisposedExtensions.IsNullOrDisposed(m_RtpClient) || m_RtpClient.IsActive is false) return false;

            //The socket is shared if there is a context using the same socket
            RtpClient.TransportContext context = m_RtpClient.GetContextBySocket(m_RtspSocket);

            return Common.IDisposedExtensions.IsNullOrDisposed(context) is false && context.IsActive;// && context.HasAnyRecentActivity;
        }
    }

    /// <summary>
    /// Indicates the amount of messages which were transmitted more then one time.
    /// </summary>
    public int RetransmittedMessages
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_ReTransmits; }
    }

    /// <summary>
    /// Indicates if the client has tried to Authenticate using the current <see cref="Credential"/>'s
    /// </summary>
    public bool TriedCredentials
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return string.IsNullOrWhiteSpace(m_AuthorizationHeader) is false; }
    }

    /// <summary>
    /// The amount of <see cref="RtspMessage"/>'s sent by this instance.
    /// </summary>
    public int MessagesSent
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_SentMessages; }
    }

    /// <summary>
    /// The amount of <see cref="RtspMessage"/>'s receieved by this instance.
    /// </summary>
    public int MessagesReceived
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_ReceivedMessages; }
    }

    /// <summary>
    /// The amount of messages pushed by the remote party
    /// </summary>
    public int MessagesPushed
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_PushedMessages; }
    }

    /// <summary>
    /// The amount of time taken to connect to the remote party.
    /// </summary>
    public TimeSpan ConnectionTime
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_ConnectionTime; }
    }

    /// <summary>
    /// The amount of time taken since the response was received to the last <see cref="RtspMessage"/> sent.
    /// </summary>
    public TimeSpan LastMessageRoundTripTime
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_LastMessageRoundTripTime; }
    }

    /// <summary>
    /// If indicated by the remote party the value of the 'delay' header from the Timestamp header.
    /// </summary>
    public TimeSpan LastServerDelay
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_LastServerDelay; }
    }

    /// <summary>
    /// Indicates if the client has been assigned a <see cref="SessionId"/>
    /// </summary>
    public bool HasSession
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_Sessions.Count() > 0; }
    }// false == string.IsNullOrWhiteSpace(m_SessionId)

    /// <summary>
    /// Gets the value of the Session header as it was seen in a response.
    /// When set will override any existing Session header previously seen.
    /// </summary>
    public string SessionId
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_SessionId; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        set { m_SessionId = value; }
    }

    /// <summary>
    /// Any SessionId's received in a response.
    /// </summary>
    public IEnumerable<string> SessionIds
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_Sessions.Keys; }
    }

    //Determine if Start and EndTime are worth having?

    /// <summary>
    /// If playing, the TimeSpan which represents the time this media started playing from.
    /// </summary>
    public TimeSpan? StartTime
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get
        {
            if (Common.IDisposedExtensions.IsNullOrDisposed(Client)) return null;

            TimeSpan? startTime = default;

            foreach (RtpClient.TransportContext tc in Client.GetTransportContexts()) if (startTime.HasValue is false || tc.m_StartTime > startTime) startTime = tc.m_StartTime;

            return startTime;
        }
    }

    /// <summary>
    /// If playing, the TimeSpan which represents the time the media will end.
    /// </summary>
    public TimeSpan? EndTime
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get
        {
            if (Common.IDisposedExtensions.IsNullOrDisposed(Client)) return null;

            TimeSpan? endTime = default;

            foreach (RtpClient.TransportContext tc in Client.GetTransportContexts()) if (endTime.HasValue is false || tc.m_EndTime > endTime) endTime = tc.m_EndTime;

            return endTime;
        }
    }

    //Remaining?

    /// <summary>
    /// If playing, indicates if the RtspClient is playing from a live source which means there is no absolute start or end time and seeking may not be supported.
    /// </summary>
    public bool LivePlay
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return EndTime.Equals(Common.Extensions.TimeSpan.TimeSpanExtensions.InfiniteTimeSpan); }
    }

    /// <summary>
    /// Indicates if there is any media being played by the RtspClient at the current time.
    /// </summary>
    public bool IsPlaying
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get
        {
            //If started playing
            if (m_Playing.Count > 0 && m_StartedPlaying.HasValue)
            {
                //Try to determine playing status from the transport and the MediaState.
                try
                {
                    System.TimeSpan? endTime = EndTime;

                    //If not playing anymore do nothing
                    if (endTime.HasValue && false.Equals(endTime.Value.Equals(Media.Common.Extensions.TimeSpan.TimeSpanExtensions.InfiniteTimeSpan)) &&
                        DateTime.UtcNow - m_StartedPlaying.Value > endTime.Value)
                    {
                        return false;
                    }

                    //If the media is playing the RtspClient is only playing if the socket is shared or the Transport is connected.
                    if (Common.IDisposedExtensions.IsNullOrDisposed(m_RtpClient)) return false;

                    //Just takes more time...
                    //foreach (RtpClient.TransportContext tc in m_RtpClient.GetTransportContexts())
                    //{
                    //    if (tc.HasAnyRecentActivity) return true;
                    //}

                    //if the client is active the RtspClient is probably playing.
                    return m_RtpClient.IsActive;

                }
                catch (Exception ex)
                {
                    Media.Common.ILoggingExtensions.Log(Logger, ToString() + "@IsPlaying - " + ex.Message);
                    Media.Common.ILoggingExtensions.LogException(Logger, ex);
                }
            }

            //The RtspClient is not playing
            return false;
        }
    }

    /// <summary>
    /// The DateTime in which the client started playing if playing, otherwise null.
    /// </summary>
    public DateTime? StartedPlaying
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_StartedPlaying; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        protected internal set
        {
            m_StartedPlaying = value;
        }
    }

    /// <summary>
    /// The amount of time in seconds the KeepAlive request will be sent to the server after connected.
    /// If a GET_PARAMETER request is not supports OPTIONS will be sent instead.
    /// </summary>
    public TimeSpan RtspSessionTimeout
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_RtspSessionTimeout; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        set
        {
            m_RtspSessionTimeout = value;

            if (m_RtspSessionTimeout <= TimeSpan.Zero)
            {
                //Don't send a request to keep the connection alive
                DisableKeepAliveRequest = true;

                m_KeepAliveTimer?.Dispose();

                m_KeepAliveTimer = null;
            }

            //This is probably wrong, the time should be relative to all requests and not just the last...
            m_KeepAliveTimer?.Change(m_LastTransmitted is not null && m_LastTransmitted.Transferred.HasValue ? (m_RtspSessionTimeout - (DateTime.UtcNow - m_LastTransmitted.Created)) : m_RtspSessionTimeout, Media.Common.Extensions.TimeSpan.TimeSpanExtensions.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Gets or Sets amount the fraction of time the client will wait during a responses for a response without blocking.
    /// If less than or equal to 0 the value 1 will be used.
    /// </summary>
    public int ResponseTimeoutInterval
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_MaximumTransactionAttempts; }
        set { m_MaximumTransactionAttempts = Binary.Clamp(value, 1, int.MaxValue); }
    }

    //The last RtspMessage received by the RtspClient from the remote EndPoint.
    public RtspMessage LastTransmitted
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_LastTransmitted; }
    }

    /// <summary>
    /// The ClientProtocolType the RtspClient is using Reliable (Tcp), Unreliable(Udp) or Http(Tcp)
    /// </summary>
    public ClientProtocolType RtspProtocol
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_RtspProtocol; }
    }

    //Should not be tied to RTP

    /// <summary>
    /// The ProtocolType the RtspClient will setup for underlying RtpClient.
    /// </summary>
    public ProtocolType RtpProtocol
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_RtpProtocol; }
    }

    /// <summary>
    /// Gets or sets the current location to the Media on the Rtsp Server and updates Remote information and ClientProtocol if required by the change.
    /// If the RtspClient was listening then it will be stopped and started again
    /// </summary>
    public Uri CurrentLocation
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_CurrentLocation; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        set
        {
            try
            {
                //If Different
                if (m_CurrentLocation != value)
                {

                    m_InitialLocation ??= value;

                    //Backup the current location, (needs history list?)
                    m_PreviousLocation = m_CurrentLocation;

                    bool wasPlaying = IsPlaying;

                    if (wasPlaying) StopPlaying();

                    m_CurrentLocation = value;

                    switch (m_CurrentLocation.HostNameType)
                    {
                        case UriHostNameType.IPv4:
                        case UriHostNameType.IPv6:

                            m_RemoteIP = IPAddress.Parse(m_CurrentLocation.DnsSafeHost);

                            break;
                        case UriHostNameType.Dns:

                            //if(System.Net.IPAddress.TryParse(m_CurrentLocation.DnsSafeHost, out m_RemoteIP))
                            //{
                            //    break;
                            //}

                            if (m_RtspSocket is not null)
                            {

                                //Will use IPv6 by default if possible.
                                m_RemoteIP = System.Net.Dns.GetHostAddresses(m_CurrentLocation.DnsSafeHost).FirstOrDefault(a => a.AddressFamily == m_RtspSocket.AddressFamily);

                                if (m_RemoteIP is null) throw new NotSupportedException("The given Location uses a HostNameType which is not the same as the underlying socket's address family. " + m_CurrentLocation.HostNameType + ", " + m_RtspSocket.AddressFamily + " And as a result no remote IP could be obtained to complete the connection.");
                            }
                            else
                            {
                                //Will use IPv6 by default if possible.
                                m_RemoteIP = System.Net.Dns.GetHostAddresses(m_CurrentLocation.DnsSafeHost).FirstOrDefault();
                            }

                            break;

                        default: throw new NotSupportedException("The given Location uses a HostNameType which is not supported. " + m_CurrentLocation.HostNameType);
                    }

                    m_RtspPort = m_CurrentLocation.Port;

                    //Validate ports, should throw? should also use default port for scheme
                    if (m_RtspPort is <= ushort.MinValue or > ushort.MaxValue) m_RtspPort = RtspMessage.ReliableTransportDefaultPort;

                    //Determine protocol
                    m_RtspProtocol = m_CurrentLocation.Scheme == RtspMessage.ReliableTransportScheme
                        ? ClientProtocolType.Tcp
                        : m_CurrentLocation.Scheme == RtspMessage.UnreliableTransportScheme ? ClientProtocolType.Udp : ClientProtocolType.Http;

                    //Make a IPEndPoint 
                    m_RemoteRtsp = new IPEndPoint(m_RemoteIP, m_RtspPort);

                    //Should take into account current time with StartTime?
                    if (wasPlaying) StartPlaying();
                }
            }
            catch (Exception ex)
            {
                Media.Common.TaggedExceptionExtensions.RaiseTaggedException(this, "Could not resolve host from the given location. See InnerException.", ex);

                throw;
            }
        }
    }

    /// <summary>
    /// Gets the Uri which was used first with this instance.
    /// </summary>
    public Uri InitialLocation
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_InitialLocation; }
    }

    /// <summary>
    /// Gets the Uri which was used directly before the <see cref="CurrentLocation"/> with this instance.
    /// </summary>
    public Uri PreviousLocation
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_PreviousLocation; }
    }

    /// <summary>
    /// Indicates if the RtspClient is connected to the remote host
    /// </summary>
    /// <notes>May want to do a partial receive for 1 byte which would take longer but indicate if truly connected. Udp may not be Connected.</notes>
    public bool IsConnected
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return Common.IDisposedExtensions.IsNullOrDisposed(this) is false && m_ConnectionTime >= TimeSpan.Zero && m_RtspSocket is not null; /*&& m_RtspSocket.Connected*/; }
    }

    /// <summary>
    /// The network credential to utilize in RtspRequests
    /// </summary>
    public NetworkCredential Credential
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_Credential; }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        set
        {
            m_Credential = value;

            m_AuthorizationHeader = null;
        }
    }

    /// <summary>
    /// The type of AuthenticationScheme to utilize in RtspRequests, if this is not set then the Credential will not send until it has been determined from a Not Authroized response.
    /// </summary>
    public AuthenticationSchemes AuthenticationScheme
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_AuthenticationScheme; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        set
        {
            if (value == m_AuthenticationScheme) return;

            switch (m_AuthenticationScheme)
            {
                case AuthenticationSchemes.Basic:
                case AuthenticationSchemes.Digest:
                case AuthenticationSchemes.None:
                    break;
                default: throw new System.InvalidOperationException("Only None, Basic and Digest are supported");
            }

            m_AuthenticationScheme = value;

            m_AuthorizationHeader = null;
        }
    }

    /// <summary>
    /// The amount of bytes sent by the RtspClient
    /// </summary>
    public int BytesSent
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_SentBytes; }
    }

    /// <summary>
    /// The amount of bytes received by the RtspClient
    /// </summary>
    public int BytesRecieved
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_ReceivedBytes; }
    }

    /// <summary>
    /// The current SequenceNumber of the RtspClient
    /// </summary>
    public int ClientSequenceNumber
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_CSeq; }
    }

    /// <summary>
    /// The current SequenceNumber of the remote RTSP party
    /// </summary>
    public int RemoteSequenceNumber
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_RCSeq; }
    }

    /// <summary>
    /// Gets the <see cref="MediaDescription"/>'s which pertain to media which is currently playing.
    /// </summary>
    public IEnumerable<MediaDescription> PlayingMedia
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_Playing.Keys.AsEnumerable(); }
    }

    /// <summary>
    /// Gets or Sets the <see cref="SessionDescription"/> describing the media at <see cref="CurrentLocation"/>.
    /// </summary>
    public SessionDescription SessionDescription
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_SessionDescription; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        protected set
        {
            //if (value is null) throw new ArgumentNullException("The SessionDescription cannot be null.");
            m_SessionDescription = value;
        }
    }

    /// <summary>
    /// The RtpClient associated with this RtspClient
    /// </summary>
    public RtpClient Client
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_RtpClient; }
    }

    /// <summary>
    /// Gets or Sets the ReadTimeout of the underlying NetworkStream / Socket (msec)
    /// </summary>
    public int SocketReadTimeout
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return Common.IDisposedExtensions.IsNullOrDisposed(this) || m_RtspSocket is null ? -1 : m_RtspSocket.ReceiveTimeout; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        set { if (Common.IDisposedExtensions.IsNullOrDisposed(this) || m_RtspSocket is null) return; m_RtspSocket.ReceiveTimeout = value; }
    }

    /// <summary>
    /// Gets or Sets the WriteTimeout of the underlying NetworkStream / Socket (msec)
    /// </summary>
    public int SocketWriteTimeout
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return Common.IDisposedExtensions.IsNullOrDisposed(this) || m_RtspSocket is null ? -1 : m_RtspSocket.SendTimeout; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        set { if (Common.IDisposedExtensions.IsNullOrDisposed(this) || m_RtspSocket is null) return; m_RtspSocket.SendTimeout = value; }
    }

    /// <summary>
    /// The UserAgent sent with every RtspRequest
    /// </summary>
    public string UserAgent
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get { return m_UserAgent; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        set { if (string.IsNullOrWhiteSpace(value)) throw new ArgumentNullException("UserAgent cannot consist of only null or whitespace."); m_UserAgent = value; }
    }

    #endregion

    #endregion

    #region Constructor / Destructor

    /// <summary>
    /// Creates a RtspClient on a non standard Rtsp Port
    /// </summary>
    /// <param name="location">The absolute location of the media</param>
    /// <param name="rtspPort">The port to the RtspServer is listening on</param>
    /// <param name="rtpProtocolType">The type of protocol the underlying RtpClient will utilize and will not deviate from the protocol is no data is received, if null it will be determined from the location Scheme</param>
    /// <param name="existing">An existing Socket</param>
    /// <param name="leaveOpen"><see cref="LeaveOpen"/></param>
    public RtspClient(Uri location, ClientProtocolType? rtpProtocolType = null, int bufferSize = DefaultBufferSize, Socket existing = null, bool leaveOpen = false, int maximumTransactionAttempts = (int)Common.Extensions.TimeSpan.TimeSpanExtensions.MicrosecondsPerMillisecond, bool shouldDispose = true)
        : base(shouldDispose)
    {
        //`Malo malam vitam boni civis`
        SendUserAgent = true;

        if (location is null) throw new ArgumentNullException(nameof(location));

        if (location.IsAbsoluteUri is false)
        {
            if (existing is null) throw new ArgumentException("Must be absolute unless a socket is given", nameof(location));
            location = existing.Connected
                ? Media.Common.Extensions.IPEndPoint.IPEndPointExtensions.ToUri(((IPEndPoint)existing.RemoteEndPoint), (existing.ProtocolType == ProtocolType.Udp ? RtspMessage.UnreliableTransportScheme : RtspMessage.ReliableTransportScheme))
                : existing.IsBound
                ? Media.Common.Extensions.IPEndPoint.IPEndPointExtensions.ToUri(((IPEndPoint)existing.LocalEndPoint), (existing.ProtocolType == ProtocolType.Udp ? RtspMessage.UnreliableTransportScheme : RtspMessage.ReliableTransportScheme))
                : throw new InvalidOperationException("location must be specified when existing socket must be connected or bound.");
        }

        //Check the Scheme
        if (location.Scheme.StartsWith(RtspMessage.MessageIdentifier, StringComparison.InvariantCultureIgnoreCase) is false
            &&
           location.Scheme.StartsWith(System.Uri.UriSchemeHttp, StringComparison.InvariantCultureIgnoreCase) is false)
            throw new ArgumentException("Uri Scheme must start with rtsp or http", "location");

        //Set the location and determines the m_RtspProtocol and IP Protocol.
        CurrentLocation = location;

        //If the client has specified a Protcol to use then use it
        if (rtpProtocolType.HasValue)
        {
            //Determine if this means anything for Rtp Transport and set the field
            m_RtpProtocol = rtpProtocolType.Value is ClientProtocolType.Tcp or ClientProtocolType.Http
                ? ProtocolType.Tcp
                : rtpProtocolType.Value == ClientProtocolType.Udp
                    ? ProtocolType.Udp
                    : throw new ArgumentException("Must be Tcp or Udp.", nameof(rtpProtocolType));
        }

        //If there is an existing socket
        if (existing is not null)
        {
            //Use it
            RtspSocket = existing;
        }

        //If no socket is given a new socket will be created

        //Check for a bufferSize of specified - unspecified value
        //Cases of anything less than or equal to 0 mean use the existing ReceiveBufferSize if possible.
        if (bufferSize <= 0) bufferSize = m_RtspSocket is not null ? m_RtspSocket.ReceiveBufferSize : 0;

        //Create the segment given the amount of memory required if possible
        if (bufferSize > 0) m_Buffer = new Common.MemorySegment(bufferSize);
        else m_Buffer = new Common.MemorySegment(m_RtpProtocol == ProtocolType.Tcp ? DefaultBufferSize + (DefaultBufferSize >> 1) : DefaultBufferSize); //Use 12288 bytes (12k), the largest message size is 65535 bytes.... the max buffer should be 65536 bytes.

        //If leave open is set the socket will not be disposed.
        LeaveOpen = leaveOpen;

        //Set the protocol version to use in requests.
        ProtocolVersion = DefaultProtocolVersion;

        //Create a RtpClient to prevent accidental errors, (would be easier for attaching logger)
        m_RtpClient = new RtpClient(m_Buffer);

        m_RtpClient.OutOfBandData += ProcessInterleavedData;

        ConfigureSocket = ConfigureRtspSocket;

        HandlePlayEvent = HandleStopEvent = true;

        m_MaximumTransactionAttempts = maximumTransactionAttempts;

        m_InterleaveEvent = new ManualResetEventSlim(true, m_SocketPollMicroseconds = m_MaximumTransactionAttempts);
    }

    /// <summary>
    /// Creates a new RtspClient from the given uri in string form.
    /// E.g. 'rtsp://somehost/sometrack/
    /// </summary>
    /// <param name="location">The string which will be parsed to obtain the Location</param>
    /// <param name="rtpProtocolType">The type of protocol the underlying RtpClient will utilize, if null it will be determined from the location Scheme</param>
    /// <param name="bufferSize">The amount of bytes the client will use during message reception, Must be at least 4096 and if larger it will also be shared with the underlying RtpClient</param>
    public RtspClient(string location, ClientProtocolType? rtpProtocolType = null, int bufferSize = DefaultBufferSize)
        : this(new Uri(location), rtpProtocolType, bufferSize) //UriDecode?
    {
        //Check for a null Credential and UserInfo in the Location given.
        if (Credential is null &&
            string.IsNullOrWhiteSpace(CurrentLocation.UserInfo) is false)
        {
            //Parse the given cred from the location
            Credential = Media.Common.Extensions.Uri.UriExtensions.ParseUserInfo(CurrentLocation);

            //Remove the user info from the location (may not have @?)
            m_InitialLocation = CurrentLocation = new Uri(CurrentLocation.AbsoluteUri.Replace(CurrentLocation.UserInfo + (char)Common.ASCII.AtSign, string.Empty).Replace(CurrentLocation.UserInfo, string.Empty));
        }
    }

    #endregion

    #region Events

    public delegate void RtspClientAction(RtspClient sender, object args);

    public delegate void RequestHandler(RtspClient sender, RtspMessage request);

    public delegate void ResponseHandler(RtspClient sender, RtspMessage request, RtspMessage response);

    public event RtspClientAction OnConnect;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    protected internal void OnConnected()
    {
        if (Common.IDisposedExtensions.IsNullOrDisposed(this)) return;

        RtspClientAction action = OnConnect;

        if (action is null) return;

        foreach (RtspClientAction handler in action.GetInvocationList().Cast<RtspClientAction>())
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception e)
            {
                Common.ILoggingExtensions.LogException(Logger, e);

                break;
            }
        }

    }

    public event RequestHandler OnRequest;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    protected internal void Requested(RtspMessage request)
    {
        if (Common.IDisposedExtensions.IsNullOrDisposed(this)) return;

        RequestHandler action = OnRequest;

        if (action is null) return;

        foreach (RequestHandler handler in action.GetInvocationList().Cast<RequestHandler>())
        {
            try { handler(this, request); }
            catch (Exception e)
            {
                Common.ILoggingExtensions.LogException(Logger, e);

                break;
            }
        }
    }

    public event ResponseHandler OnResponse; // = m_LastTransmitted...

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    protected internal void Received(RtspMessage request, RtspMessage response)
    {
        if (Common.IDisposedExtensions.IsNullOrDisposed(this)) return;

        ResponseHandler action = OnResponse;

        if (action is null) return;

        foreach (ResponseHandler handler in action.GetInvocationList().Cast<ResponseHandler>())
        {
            try { handler(this, request, response); }
            catch (Exception e)
            {
                Common.ILoggingExtensions.LogException(Logger, e);

                break;
            }
        }
    }

    //public event ResponseHandler OnAuthenticate;

    //[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    //internal protected void Authenticate(RtspMessage request, RtspMessage response)
    //{
    //    if (IsDisposed) return;

    //    ResponseHandler action = OnAuthenticate;

    //    if (action is null) return;

    //    foreach (ResponseHandler handler in action.GetInvocationList())
    //    {
    //        try { handler(this, request, response); }
    //        catch (Exception e)
    //        {
    //            Common.ILoggingExtensions.LogException(Logger, e);

    //            break;
    //        }
    //    }
    //}

    public event RtspClientAction OnDisconnect;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal void OnDisconnected()
    {
        if (Common.IDisposedExtensions.IsNullOrDisposed(this)) return;

        RtspClientAction action = OnDisconnect;

        if (action is null) return;

        foreach (RtspClientAction handler in action.GetInvocationList().Cast<RtspClientAction>())
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception e)
            {
                Common.ILoggingExtensions.LogException(Logger, e);

                break;
            }
        }
    }

    public event RtspClientAction OnPlay;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    protected internal void OnPlaying(MediaDescription mediaDescription = null)
    {
        if (Common.IDisposedExtensions.IsNullOrDisposed(this)) return;

        //Is was not already playing then set the value
        if (HandlePlayEvent && m_StartedPlaying.HasValue is false)
        {
            //Set started playing
            m_StartedPlaying = DateTime.UtcNow;

            //Active the RtpClient
            //if (false == Common.IDisposedExtensions.IsNullOrDisposed(m_RtpClient)) m_RtpClient.Activate();
        }

        RtspClientAction action = OnPlay;

        if (action is null) return;

        if (mediaDescription is not null)
        {
            MediaSessionState mss = m_Playing[mediaDescription];

            if (mss is not null)
            {
                mss.Status = MediaStatus.Playing;
            }
        }
        else
        {
            foreach (MediaSessionState mss in m_Playing.Values)
            {
                mss.Status = MediaStatus.Playing;
            }
        }

        foreach (RtspClientAction handler in action.GetInvocationList().Cast<RtspClientAction>())
        {
            try { handler(this, mediaDescription); }
            catch (Exception e)
            {
                Common.ILoggingExtensions.LogException(Logger, e);

                break;
            }
        }
    }

    public event RtspClientAction OnStop;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    protected internal void OnStopping(MediaDescription mediaDescription = null)
    {
        if (Common.IDisposedExtensions.IsNullOrDisposed(this)) return;

        //Is was already playing then set the value
        if (HandleStopEvent &&
            Common.IDisposedExtensions.IsNullOrDisposed(mediaDescription) &&
            m_StartedPlaying.HasValue || m_Playing.Count is 0) m_StartedPlaying = null;

        RtspClientAction action = OnStop;

        if (action is null) return;

        if (mediaDescription is not null)
        {
            MediaSessionState mss = m_Playing[mediaDescription];

            if (mss is not null)
            {
                mss.Status = MediaStatus.Stopped;
            }
        }
        else
        {
            foreach (MediaSessionState mss in m_Playing.Values)
            {
                mss.Status = MediaStatus.Stopped;
            }
        }

        foreach (RtspClientAction handler in action.GetInvocationList().Cast<RtspClientAction>())
        {
            try { handler(this, mediaDescription); }
            catch (Exception e)
            {
                Common.ILoggingExtensions.LogException(Logger, e);

                break;
            }
        }
    }

    public event RtspClientAction OnPause;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    protected internal void OnPausing(MediaDescription mediaDescription = null)
    {
        if (Common.IDisposedExtensions.IsNullOrDisposed(this)) return;

        RtspClientAction action = OnPause;

        if (action is null) return;

        if (mediaDescription is not null)
        {
            MediaSessionState mss = m_Playing[mediaDescription];

            if (mss is not null)
            {
                mss.Status = MediaStatus.Paused;
            }
        }
        else
        {
            foreach (MediaSessionState mss in m_Playing.Values)
            {
                mss.Status = MediaStatus.Playing;
            }
        }

        foreach (RtspClientAction handler in action.GetInvocationList())
        {
            try { handler(this, mediaDescription); }
            catch (Exception e)
            {
                Common.ILoggingExtensions.LogException(Logger, e);

                break;
            }
        }
    }

    //OnDescribe / DescriptionRecieved

    // => OnDescriptionRecieved(SessionDescription description)

    //OnBufferRecieved
    //OnBufferCompleted

    #endregion

    #region Methods

    /// <summary>
    /// DisconnectsSockets, Connects and optionally reconnects the Transport if reconnectClient is true.
    /// </summary>
    /// <param name="reconnectClient"></param>
    protected internal virtual void Reconnect(bool reconnectClient = true)
    {
        DisconnectSocket();

        Connect();

        if (reconnectClient && IsPlaying && m_RtpClient.IsActive is false) m_RtpClient.Activate();

        m_AuthorizationHeader = null;
    }

    //ProcessRemoteAnnounce

    protected internal virtual void ProcessRemoteGetParameter(RtspMessage get)
    {
        //Todo, Handle other parameters

        //Make a response
        using (var response = new RtspMessage(RtspMessageType.Response, get.Version, get.ContentEncoding))
        {
            //Set the sequence number
            response.CSeq = get.CSeq;

            //Send it
            using (SendRtspMessage(response, false, false)) ;
        }
    }

    protected internal virtual void ProcessRemoteSetParameter(RtspMessage set)
    {
        //MS-RTSP Send a server sent request similar to as follows

        /*
        SET_PARAMETER Location RTSP/1.0
        Content-Type: application/x-wms-extension-cmd
        X-Notice: 2101 "End-of-Stream Reached"
        RTP-Info: Track1, Track2
        X-Playlist-Gen-Id: 358
        Content-Length: 41
        Date: Wed, 04 Feb 2015 19:47:21 GMT
        CSeq: 1
        User-Agent: WMServer/9.1.1.5001\r\n(\r\n) [Breaks Wireshark and causes a freeze until the analyzer can recover (if it does)]
        Session: 6312817326953834859
        EOF: true
         */

        bool effectedMedia = false;

        string contentType = set[RtspHeaders.ContentType];

        if (false == string.IsNullOrWhiteSpace(contentType))
        {
            contentType = contentType.Trim();

            #region [MSRTSP - application/x-wms-extension-cmd]

            if (string.Compare(contentType, "application/x-wms-extension-cmd", true) is 0)
            {
                string xNotice = set["X-Notice"];

                if (false == string.IsNullOrWhiteSpace(xNotice)) //&& Boolean.Parse(set["EOF"])
                {
                    string[] parts = xNotice.Trim().Split(RtspMessage.SpaceSplit, 2);

                    //Get rid of anything unrelated
                    string noticeIdValue = parts.FirstOrDefault();

                    //If something was extracted attempt to parse
                    if (false == string.IsNullOrWhiteSpace(noticeIdValue))
                    {

                        //If the noticeId is 2101
                        if (int.TryParse(noticeIdValue, out int noticeId) &&
                            noticeId == 2101)
                        {
                            //End Of Stream notice?

                            //Get the rtp-info header
                            string rtpInfo = set[RtspHeaders.RtpInfo];


                            //Make a parser class which can be reused?

                            //If parsing of the header succeeded
                            if (RtspHeaders.TryParseRtpInfo(rtpInfo, out string[] rtpInfos))
                            {
                                //Notes that more then 1 value here indicates AggregateControl is supported at the server but possibly not the session?

                                //Loop all found sub header values
                                foreach (string rtpInfoValue in rtpInfos)
                                {




                                    //If any value which was needed was found.
                                    if (RtspHeaders.TryParseRtpInfo(rtpInfoValue, out Uri uri, out int? seq, out int? rtpTime, out int? ssrc) && seq.HasValue)
                                    {
                                        //Just use the ssrc to lookup the context.
                                        if (ssrc.HasValue)
                                        {
                                            //Get the context created with the ssrc defined above
                                            RtpClient.TransportContext context = m_RtpClient.GetContextBySourceId(ssrc.Value);

                                            //If that context is not null then allow it's ssrc to change now.
                                            if (context is not null)
                                            {
                                                if (m_Playing.Remove(context.MediaDescription))
                                                {

                                                    effectedMedia = true;

                                                    OnStopping(context.MediaDescription);

                                                    //m_RtpClient.SendGoodbye(context, null, context.SynchronizationSourceIdentifier, false);
                                                }

                                                context = null;
                                            }
                                            else
                                            {
                                                Common.ILoggingExtensions.Log(Logger, "Unknown context for ssrc = " + ssrc.Value);
                                            }
                                        }
                                        else if (uri is not null)
                                        {
                                            //Need to get the context by the uri.
                                            //Location = rtsp://abc.com/live/movie
                                            //uri = rtsp://abc.com/live/movie/trackId=0
                                            //uri = rtsp://abc.com/live/movie/trackId=1
                                            //uri = rtsp://abc.com/live/movie/trackId=2

                                            //Get the context created with from the media description with the same resulting control uri
                                            RtpClient.TransportContext context = m_RtpClient.GetTransportContexts().FirstOrDefault(tc => tc.MediaDescription.GetAbsoluteControlUri(CurrentLocation, SessionDescription) == uri);

                                            //If that context is not null then allow it's ssrc to change now.
                                            if (context is not null)
                                            {
                                                //The last packet will have the sequence number of seq.Value

                                                if (m_Playing.Remove(context.MediaDescription))
                                                {
                                                    effectedMedia = true;

                                                    OnStopping(context.MediaDescription);

                                                    //m_RtpClient.SendGoodbye(context, null, context.SynchronizationSourceIdentifier, false);
                                                }

                                                context = null;
                                            }
                                            else
                                            {
                                                Common.ILoggingExtensions.Log(Logger, "Unknown context for Uri = " + uri.AbsolutePath);
                                            }

                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            #endregion
        }

        //Todo, Handle other parameters in the body

        //Make a response
        using (var response = new RtspMessage(RtspMessageType.Response, set.Version, set.ContentEncoding))
        {
            //Indicate OK
            response.RtspStatusCode = RtspStatusCode.OK;

            //Set the sequence number
            response.CSeq = set.CSeq;

            //Send it
            using (SendRtspMessage(response, false, false)) ;
        }


        //Check that only the rtx media is playing and then remove session if so

        if (effectedMedia && m_Playing.Count is 1 && m_Playing.Keys.First().GetAbsoluteControlUri(CurrentLocation, SessionDescription).AbsoluteUri.EndsWith("rtx", StringComparison.OrdinalIgnoreCase))
        {
            //RemoveSession(m_SessionId);

            StopPlaying();
        }

    }

    protected internal virtual void ProcessRemoteEndOfStream(RtspMessage message)
    {
        //Not playing so we dont care
        if (false == IsPlaying) return;

        /* https://tools.ietf.org/html/draft-zeng-rtsp-end-of-stream-00
        An END_OF_STREAM request MUST include "CSeq", "Range" and "Session"
    headers.
    It SHOULD include "RTP-Info" header.
    The RTP-Info in server's END_OF_STREAM request
    is used to indicate the sequence number of
    the ending RTP packet for each media stream.

    An END_OF_STREAM requet MAY include a new "Reason" header,
    defined as a
    string, whose purpose is to allow the server to explain why stream
    has ended, and whose ABNF definition is given below:

            Reason     =  "Reason" ":"   Reason-Phrase CRLF
        */

        //Ensure Range and RtpInfo are present.
        string range = message[RtspHeaders.Range],

            rtpInfo = message[RtspHeaders.RtpInfo];

        if (string.IsNullOrWhiteSpace(range) || string.IsNullOrWhiteSpace(rtpInfo)) return;

        //Check what is ending...
        //if (m_LastTransmitted.Location == RtspMessage.Wildcard)
        //{

        //}else{
        // Must get stream by location to ensure request is relevent.
        //}


        //Make a parser class which can be reused?

        //If parsing of the header succeeded
        if (RtspHeaders.TryParseRtpInfo(rtpInfo, out string[] rtpInfos))
        {
            //Notes that more then 1 value here indicates AggregateControl is supported at the server but possibly not the session?

            //Loop all found sub header values
            foreach (string rtpInfoValue in rtpInfos)
            {




                //If any value which was needed was found.
                if (RtspHeaders.TryParseRtpInfo(rtpInfoValue, out Uri uri, out int? seq, out int? rtpTime, out int? ssrc))
                {
                    //Just use the ssrc to lookup the context.
                    if (ssrc.HasValue)
                    {
                        //Get the context created with the ssrc defined above
                        RtpClient.TransportContext context = m_RtpClient.GetContextBySourceId(ssrc.Value);

                        //If that context is not null then allow it's ssrc to change now.
                        if (context is not null)
                        {
                            if (m_Playing.Remove(context.MediaDescription))
                            {
                                OnStopping(context.MediaDescription);

                                m_RtpClient.SendGoodbye(context, null, context.SynchronizationSourceIdentifier, false);
                            }

                            context = null;
                        }
                    }
                    else if (uri is not null)
                    {
                        //Need to get the context by the uri.
                        //Location = rtsp://abc.com/live/movie
                        //uri = rtsp://abc.com/live/movie/trackId=0
                        //uri = rtsp://abc.com/live/movie/trackId=1
                        //uri = rtsp://abc.com/live/movie/trackId=2

                        //Get the context created with from the media description with the same resulting control uri
                        RtpClient.TransportContext context = m_RtpClient.GetTransportContexts().FirstOrDefault(tc => tc.MediaDescription.GetAbsoluteControlUri(CurrentLocation, SessionDescription) == uri);

                        //If that context is not null then allow it's ssrc to change now.
                        if (context is not null)
                        {
                            if (m_Playing.Remove(context.MediaDescription))
                            {
                                OnStopping(context.MediaDescription);

                                m_RtpClient.SendGoodbye(context, null, context.SynchronizationSourceIdentifier, false);
                            }

                            context = null;
                        }

                    }
                }
            }
        }

        //Make a response
        using (var response = new RtspMessage(RtspMessageType.Response, message.Version, message.ContentEncoding))
        {
            //Indicate OK
            response.RtspStatusCode = RtspStatusCode.OK;

            //Set the sequence number
            response.CSeq = message.CSeq;

            //Send it
            using (var serverResponse = SendRtspMessage(response, false, false))
            {

                if (m_Sessions.TryGetValue(m_SessionId, out RtspSession related))
                {
                    related.UpdatePushedMessages(message, serverResponse);

                    related = null;
                }
            }
        }

        StopPlaying();
    }

    //could handle EndOfStream as a PlayNotify...

    protected internal virtual void ProcessRemoteTeardown(RtspMessage teardown)
    {
        //If playing
        if (false == IsPlaying) return;

        //Check if everything is stopping
        if (Uri.Equals(teardown.Location, CurrentLocation) || teardown.Location == RtspMessage.Wildcard)
        {
            OnStopping();

            m_RtpClient.SendGoodbyes();
        }
        else//Use the Uri to determine what is shutting down
        {
            //Attempt to find the media by the uri given.
            Sdp.MediaType mediaType = Sdp.MediaType.unknown;

            //uri's follow the format /.././Type
            if (Enum.TryParse<Sdp.MediaType>(teardown.Location.Segments.Last(), true, out mediaType))
            {
                //Find a contet for the type given
                var context = Client.GetTransportContexts().FirstOrDefault(tc => tc.MediaDescription.MediaType == mediaType);

                //If a context was found
                if (context is not null)
                {
                    //If it was playing
                    if (m_Playing.ContainsKey(context.MediaDescription))
                    {
                        //Indicate this media is stopping now
                        OnStopping(context.MediaDescription);

                        m_RtpClient.SendGoodbye(context, null, context.SynchronizationSourceIdentifier, false);
                    }

                    //remove the reference to the context
                    context = null;
                }
            }
        }

        //Make a response
        using (var response = new RtspMessage(RtspMessageType.Response, teardown.Version, teardown.ContentEncoding))
        {
            //Indicate OK
            response.RtspStatusCode = RtspStatusCode.OK;

            //Set the sequence number
            response.CSeq = teardown.CSeq;

            //Send it
            using (RtspMessage serverResponse = SendRtspMessage(response, false, false))
            {

                if (m_Sessions.TryGetValue(m_SessionId, out RtspSession related))
                {
                    related.UpdatePushedMessages(teardown, serverResponse);

                    related = null;
                }
            }
        }
    }

    //https://www.ietf.org/proceedings/60/slides/mmusic-8.pdf
    //Announce? Maybe it's depreceated maybe it's not...
    //Event codes are in the header.?
    //WTF is play_notify then...

    /*
     
    +-------------+-------------------------+---------------------------+
    | Notice-code | Notice-string           | Description               |
    +-------------+-------------------------+---------------------------+
    | 1103        | Playout Stalled         | -/-                       |
    |             |                         |                           |
    | 1104        | Playout Resumed         | Temporarily stopped       |
    |             |                         |                           |
    | 2101        | End-of-Stream Reached   | Content terminated        |
    |             |                         |                           |
    | 2103        | Transition              | In transition             |
    |             |                         |                           |
    | 2104        | Start-of-Stream Reached | Returned to the initial   |
    |             |                         | content                   |
    |             |                         |                           |
    | 2306        | Continuous Feed         | Live finished             |
    |             | Terminated              |                           |
    |             |                         |                           |
    | 2401        | Ticket Expired          | Viewing right expired     |
    |             |                         |                           |
    | 4400        | Error Reading Content   | Data read error           |
    |             | Data                    |                           |
    |             |                         |                           |
    | 5200        | Server Resource         | Resource cannot be        |
    |             | Unavailable             | obtained                  |
    |             |                         |                           |
    | 5401        | Downstream Failure      | Stream could not be       |
    |             |                         | obtained                  |
    |             |                         |                           |
    | 5402        | Client Session          | -/-                       |
    |             | Terminated              |                           |
    |             |                         |                           |
    | 5403        | Server Shutting Down    | -/-                       |
    |             |                         |                           |
    | 5404        | Internal Server Error   | -/-                       |
    |             |                         |                           |
    | 5501        | End-of-Window_term      | -/-                       |
    |             |                         |                           |
    | 5502        | End-of-Contract_term    | -/-                       |
    +-------------+-------------------------+---------------------------+
     
     */

    protected internal virtual void ProcessRemotePlayNotify(RtspMessage playNotify)
    {
        //Make a response
        using (var response = new RtspMessage(RtspMessageType.Response, playNotify.Version, playNotify.ContentEncoding))
        {
            //Indicate OK
            response.RtspStatusCode = RtspStatusCode.OK;

            //Set the sequence number
            response.CSeq = playNotify.CSeq;

            //Send it
            using (RtspMessage serverResponse = SendRtspMessage(response, false, false))
            {

                //should use playNotify SessionHeader.

                if (m_Sessions.TryGetValue(m_SessionId, out RtspSession related))
                {
                    related.UpdatePushedMessages(playNotify, serverResponse);

                    related = null;
                }
            }
        }
    }

    protected internal virtual void ProcessServerSentRequest(RtspMessage toProcess = null)
    {
        if (false == IgnoreServerSentMessages &&
            (toProcess is null ||
            toProcess.RtspMessageType != RtspMessageType.Request ||
            false == toProcess.IsComplete)) return;

        //Ensure suported methods contains the method requested.
        SupportedMethods.Add(toProcess.MethodString);

        //Check the sequence number
        int sequenceNumber = toProcess.CSeq;

        //Don't handle a request with an invalid remote sequence number
        if (sequenceNumber < m_RCSeq) return;

        //Update the remote sequence number
        m_RCSeq = sequenceNumber;

        //Increment handled pushed messages
        ++m_PushedMessages;

        //Raise an event for the request received.
        Received(toProcess, null);

        //Determine 
        string session = m_LastTransmitted[RtspHeaders.Session];

        if (false == string.IsNullOrWhiteSpace(m_SessionId) &&
            false == string.IsNullOrWhiteSpace(session))
        {
            //Not for the same session
            if (false.Equals(m_SessionId.Equals(session.Trim())))
            {
                return;
            }
        }

        //handle the message received
        switch (toProcess.RtspMethod)
        {
            case RtspMethod.TEARDOWN:
                {
                    ProcessRemoteTeardown(toProcess);

                    return;
                }
            case RtspMethod.ANNOUNCE:
                {
                    //https://www.ietf.org/proceedings/60/slides/mmusic-8.pdf
                    //Announce is sometimes used for this, special EventType header.

                    //Check for codes present...

                    if (Uri.Equals(toProcess.Location, CurrentLocation))
                    {
                        //Check for SDP content type and update the SessionDescription
                    }

                    return;
                }
            case RtspMethod.GET_PARAMETER:
                {

                    ProcessRemoteGetParameter(toProcess);

                    return;
                }
            case RtspMethod.SET_PARAMETER:
                {
                    ProcessRemoteSetParameter(toProcess);

                    break;
                }
            case RtspMethod.PLAY_NOTIFY:
                {
                    /*
                     There are two ways for the client to be informed about changes of
                    media resources in Play state.  The client will receive a PLAY_NOTIFY
                    request with Notify-Reason header set to media-properties-update (see
                    Section 13.5.2.  The client can use the value of the Media-Range to
                    decide further actions, if the Media-Range header is present in the
                    PLAY_NOTIFY request.  The second way is that the client issues a
                    GET_PARAMETER request without a body but including a Media-Range
                    header.  The 200 OK response MUST include the current Media-Range
                    header (see Section 18.30).
                     */

                    //Use the Uri to determine what is chaning.
                    if (Uri.Equals(toProcess.Location, CurrentLocation))
                    {
                        //See what is being notified.
                    }

                    break;
                }
            default:
                {

                    //Something else...
                    if (string.Compare(toProcess.MethodString, "END_OF_STREAM", true) is 0)
                    {
                        //Should be merged with Teardown.
                        ProcessRemoteEndOfStream(toProcess);

                        return;
                    }

                    //Make a response to indicate the method is not supported
                    using (var response = new RtspMessage(RtspMessageType.Response, toProcess.Version, toProcess.ContentEncoding))
                    {
                        //Indicate Not Allowed.
                        response.RtspStatusCode = RtspStatusCode.NotImplemented;

                        //Todo
                        //Should use MethodNotAllowed and set Allow header with supported methods.

                        //Set the sequence number
                        response.CSeq = toProcess.CSeq;

                        //Send it
                        using (var serverResponse = SendRtspMessage(response, false, false))
                        {

                            if (string.IsNullOrWhiteSpace(session) is false && m_Sessions.TryGetValue(session, out RtspSession related))
                            {
                                related.UpdatePushedMessages(toProcess, serverResponse);

                                related = null;
                            }
                        }
                    }


                    return;
                }
        }
    }

    //struct key
    //{
    //    int id;

    //    int length;

    //    List<Common.MemorySegment> parts;

    //    bool Complete
    //    {
    //        get { return parts.Sum(c => c.Count).Equals(length); }
    //    }
    //}

    //Dictionary<key, Common.MemorySegment>! continuations

    /// <summary>
    /// Handles Interleaved Data for the RtspClient by parsing the given memory for a valid RtspMessage.
    /// </summary>
    /// <param name="sender">The RtpClient instance which called this method</param>
    /// <param name="memory">The memory to parse</param>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal void ProcessInterleavedData(object sender, byte[] data, int offset, int length)
    {
        if (length <= 0 ||
            Common.Extensions.Array.ArrayExtensions.IsNullOrEmpty(data) ||
            offset + length >= data.Length ||
            Common.IDisposedExtensions.IsNullOrDisposed(this)) return;

        //Todo, it's possible a new varialbe could be passed to indicate this is binary data or a continuation..

        //if (continuation)
        //{
        //    if(continations.containskey => add => Common.MemorySegment.CreateCopy(data, offset, length, true)
        //    else continuations[key].parts.add => Common.MemorySegment.CreateCopy(data, offset, length, true))
        //}

        //thus it is possible to salvage the data in the event such is truncated by then waiting for the remaining amount of data
        //When complete then it can be evented further...

        try
        {


            //Cache offset and count, leave a register for received data (should be calulated with length)
            int received = 0;

            //Must contain textual data to be an interleaved rtsp request.
            //if (!Utility.FoundValidUniversalTextFormat(data, ref offset, ref length)) return; //See comments below if attempting to complete large packets.

            //Should check for BigEndianFrameControl @ offset which indicates a large packet or a packet under 8 bytes or a packet not handled by the RtpClient.
            //In such a case the length needs to be read and if the packet was larger than the buffer the next time this event fires the remaining data will be given
            //When reading sizes the frame size should ALWAYS be <= any Blocksize the server responded with (if any)
            //Then the data can be given back to the RtpClient with ProcessFrameData when the packet is complete.
            //If another packet arrives while one is being completed that is up to the implementation to deal with for now, other implementations just drop the data and give you no way to even receive it.            
            //Some servers may use this type of data to indicate special processing e.g. WMS for PacketPairs or RDT etc.

            unchecked
            {
                //Validate the data received
                RtspMessage interleaved = new(data, offset, length);

                //Determine what to do with the interleaved message
                switch (interleaved.RtspMessageType)
                {
                    //Handle new requests or responses
                    case RtspMessageType.Request:
                    case RtspMessageType.Response:
                        {
                            //Calculate the length of what was received
                            received = length;

                            if (received <= 0) goto default;
                            //Increment for messages received
                            ++m_ReceivedMessages;

                            //If not playing an interleaved stream, Complete the message if not complete (Should maybe check for Content-Length)
                            //Idea was to allow `Disposed` message to potentially be completed and then not disposed anymore....

                            while (Common.IDisposedExtensions.IsNullOrDisposed(this) is false &&
                                SharesSocket is false &&
                                interleaved.IsComplete is false)
                            {
                                //Take in some bytes from the socket or buffer.
                                int justReceived = interleaved.CompleteFrom(m_RtspSocket, m_Buffer);

                                if (justReceived is 0) break;

                                //Incrment for justReceived
                                received += justReceived;

                                //Ensure we are not doing to much receiving
                                if (received > RtspMessage.MaximumLength)
                                {
                                    if (Common.IDisposedExtensions.IsNullOrDisposed(interleaved) &&
                                        interleaved.IsPersistent)
                                    {
                                        Common.BaseDisposable.SetShouldDispose(interleaved, false);
                                    }

                                    break;
                                }
                            }

                            //Update counters
                            m_ReceivedBytes += received;

                            //Disposes the last message if it exists.
                            if (received > 0 && Common.IDisposedExtensions.IsNullOrDisposed(m_LastTransmitted) is false)
                            {
                                m_LastTransmitted.Dispose();

                                m_LastTransmitted = null;
                            }

                            //Store the last message
                            //m_LastTransmitted = interleaved;

                            //Need a method to get a Session by a Message.
                            //Update the messge on the session..

                            //if the message was a request and is complete handle it now.
                            if (interleaved.RtspMessageType == RtspMessageType.Request &&
                                false == InUse)
                            {
                                ProcessServerSentRequest(interleaved);
                            }

                            goto default;
                        }
                    case RtspMessageType.Invalid:
                        {
                            received = interleaved.Length;

                            //If playing and interleaved stream AND the last transmitted message is NOT null and is NOT Complete then attempt to complete it
                            if (received < length &&
                                IDisposedExtensions.IsNullOrDisposed(this) is false &&
                                Common.IDisposedExtensions.IsNullOrDisposed(m_LastTransmitted) is false /*&& interleaved.StatusLineParsed*/)
                            {
                                //RtspMessage local = m_LastTransmitted;

                                //Take note of the length of the last transmitted message.
                                int lastLength = received;

                                //Create a memory segment and complete the message as required from the buffer.
                                using var memory = new Media.Common.MemorySegment(data, offset, length);

                                //Use the data received to complete the message and not the socket
                                int justReceived = IDisposedExtensions.IsNullOrDisposed(m_LastTransmitted) is false ? m_LastTransmitted.CompleteFrom(null, memory) : 0;

                                //If anything was received
                                if (justReceived > 0)
                                {
                                    //Account for what was just received.
                                    received += justReceived;

                                    //No data was actually consumed don't raise another event.
                                    if (IDisposedExtensions.IsNullOrDisposed(m_LastTransmitted) is false && lastLength.Equals(received)) received = 0;
                                }

                                //handle the completion of a request sent by the server if allowed.
                                if (received > 0 &&
                                    IDisposedExtensions.IsNullOrDisposed(interleaved) is false &&
                                    interleaved.RtspMessageType is RtspMessageType.Request
                                    /* && InUse is false*/) //dont handle if waiting for a resposne...
                                {
                                    //Process the pushed message
                                    ProcessServerSentRequest(interleaved);

                                    //then continue
                                }
                            }

                            //Handle with default logic.
                            goto default;
                        }
                    default:
                        {
                            //If anything was received
                            //if (received < 0) return;
                            //Todo could have TransactionCache with requests which are then paried to response here.

                            //Assign
                            m_LastTransmitted = interleaved;

                            //Release the m_Interleaved event if it was set
                            if (InUse)
                            {
                                //Thus allowing threads blocked by it to proceed.
                                m_InterleaveEvent.Set();
                            } //Otherwise
                            else if (IDisposedExtensions.IsNullOrDisposed(interleaved) is false &&
                                interleaved.RtspMessageType is RtspMessageType.Response) //and was a response
                            {
                                //Otherwise indicate a message has been received now. (for responses only)
                                Received(interleaved, null);

                                //Todo, Event => CloseRequested...
                                ////string connection = m_LastTransmitted.GetHeader(RtspHeaders.Connection);

                                ////if (string.IsNullOrWhiteSpace(connection) is false && connection.IndexOf("close", StringComparison.InvariantCultureIgnoreCase) >= 0)
                                ////{
                                ////    Disconnect(true);

                                ////    if (AutomaticallyReconnect)
                                ////    {
                                ////        Connect();
                                ////    }
                                ////}

                            }

                            //Handle any data remaining in the buffer
                            //(Must ensure Length property of RtspMessage is exact).
                            //TODO, Create a test...
                            //If there is more lower data here than it possibly could be Rtp it should be handled by passing to the RtpClient for processing.
                            //The reason why its not is that the RtpClient only passed up chunks at a time and it would already have the other data in its buffer.
                            if (received > 0) ProcessInterleavedData(sender, data, offset + received, length - received);

                            //done
                            return;
                        }
                }
            }
        }
        catch (Exception ex)
        {
            Media.Common.ILoggingExtensions.LogException(Logger, ex);
        }
    }

    /// <summary>
    /// Increments and returns the current <see cref="ClientSequenceNumber"/>
    /// </summary>
    internal int NextClientSequenceNumber() { return Interlocked.Increment(ref m_CSeq); }

    //Determine if throwing exceptions are proper here.
    //Should have end time also?
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public void StartPlaying(TimeSpan? start = null, TimeSpan? end = null, IEnumerable<Sdp.MediaType> mediaTypes = null) //should allow to re describe ,bool forceDescribe = false
    {
        //Is already playing don't do anything
        if (IsPlaying) return;

        //Try to connect if not already connected.
        if (IsConnected is false) Connect();

        //Send the options if nothing was received before
        if (m_ReceivedMessages is 0) using (RtspMessage optionsResponse = SendOptions())
            {
                if (Common.IDisposedExtensions.IsNullOrDisposed(optionsResponse) ||
                    optionsResponse.ParsedProtocol.Equals(RtspMessage.MessageIdentifier) && // Fake protection from GStreamer
                    optionsResponse.RtspStatusCode > RtspStatusCode.OK) Media.Common.TaggedExceptionExtensions.RaiseTaggedException(optionsResponse, "Options Response was null or not OK. See Tag.");
                else optionsResponse.IsPersistent = false;
            }

        //Check for automatic disconnect
        if (AutomaticallyDisconnectAfterStartPlaying) Disconnect(true);

        //Check if Describe is allowed or that a SessionDescription is present.
        if (SupportedMethods.Count > 0 && false.Equals(SupportedMethods.Contains(RtspMethod.DESCRIBE.ToString())) &&
            Common.IDisposedExtensions.IsNullOrDisposed(SessionDescription) && SupportedFeatures.Count is 0)
        {
            Media.Common.TaggedExceptionExtensions.RaiseTaggedException(SupportedMethods, "SupportedMethods does not allow Describe and SessionDescription is null. See Tag with SupportedMessages.");
        }

        //Determine if any context was present or created.
        bool hasContext = false, triedAgain = false;

        Describe:
        RtspStatusCode currentStatusCode = RtspStatusCode.Unknown;
        try
        {
            //Send describe if we need a session description
            using (var describe = SendDescribe())
            {
                //Some servers will not provide a descibe when it was previously provided on the same connection
                if (describe is null) goto Setup;

                //The description is no longer needed
                describe.IsPersistent = false;

                if (Common.IDisposedExtensions.IsNullOrDisposed(describe) ||
                    describe.RtspStatusCode == RtspStatusCode.Unknown ||
                    !describe.IsSuccessful) Media.Common.TaggedExceptionExtensions.RaiseTaggedException(describe, "Describe Response was null or not OK. See Tag.");

                currentStatusCode = (RtspStatusCode)describe.StatusCode;
            }
        }
        catch (Exception ex)
        {
            //Can't get a response to the DESCRIBE
            Common.ILoggingExtensions.LogException(Logger, ex);
            triedAgain = true;
        }

        if (IsConnected is false || Common.IDisposedExtensions.IsNullOrDisposed(this)) return;

        Setup:

        switch (currentStatusCode)
        {
            default:
                {
                    if (Common.IDisposedExtensions.IsNullOrDisposed(SessionDescription))
                    {
                        if (triedAgain) return;
                        goto Describe;
                    }
                    break;
                }
            case RtspStatusCode.Unauthorized:
                {
                    return;
                }
        }

        //Not a HashSet to allow duplicates...
        List<MediaDescription> setupMedia = [];

        //Get the media descriptions in the session description to setup
        IEnumerable<MediaDescription> toSetup = SessionDescription.MediaDescriptions;

        //////Should check the TimeDescriptions to ensure media is active (Before chaning the order)
        ////if (SessionDescription.TimeDescriptions.Count() > 0)
        ////{

        ////}

        //Windows Media Server May require an order like so.
        //SetupOrder = (mds) => mds.OrderBy(md=> md.MediaType).Reverse();

        //If a SetupOrder has been defined then use it
        SetupOrder?.Invoke(toSetup);

        //For each MediaDescription in the SessionDecscription (ordered by the media type) and then reversed to ensure wms rtx going first (but it doesn't seem to matter anyway)
        //What could be done though is to use the detection of the rtx track to force interleaved playback.
        //Todo, use for to allow for parsing in flight...
        foreach (Sdp.MediaDescription md in toSetup)
        {
            if (Common.IDisposedExtensions.IsNullOrDisposed(this)) return;

            //Don't setup unwanted streams
            if (mediaTypes is not null && mediaTypes.Contains(md.MediaType) is false) continue;

            //Should be able to be SETUP if in the MediaDescriptions....
            //if (md.MediaType == MediaType.application) continue;

            //If transport was already setup then see if the transport has a context for the media
            if (Common.IDisposedExtensions.IsNullOrDisposed(Client) is false)
            {
                //Get the context for the media
                var context = Client.GetContextForMediaDescription(md);

                //If there is a context which is not null
                if (context is not null)
                {
                    //which is not already playing
                    if (m_Playing.ContainsKey(context.MediaDescription) is false)
                    {
                        //If the context is no longer receiving (should be a property on TransportContext but when pausing the RtpClient doesn't know about this)
                        if (Common.IDisposedExtensions.IsNullOrDisposed(context) ||
                            context.TimeReceiving.Equals(context.TimeSending) && context.TimeSending.Equals(Media.Common.Extensions.TimeSpan.TimeSpanExtensions.InfiniteTimeSpan))
                        {
                            //Remove the context
                            Client.TryRemoveContext(context);

                            //Dispose it
                            context.Dispose();

                            //remove the reference
                            context = null;
                        }
                        //else context.Goodbye = null;
                    }
                    else
                    {
                        //The media is already setup.
                        setupMedia.Add(md);

                        //The media is already playing
                        hasContext = true;

                        continue;
                    }
                }
            }

            int setupAttempts = 0;

            //Send a setup while there was a bad request or no response.
            do using (RtspMessage setup = SendSetup(md))
                {
                    if (Common.IDisposedExtensions.IsNullOrDisposed(this)) return;

                    #region Unused feature [Continue Without Setup Response]

                    //If the setup doesn't get a response sometimes the source starts sending data anyway....
                    //Happens especially when a rtspclient uses TCP, disconnects and then re-connects using a new socket...
                    //This no longer occurs unless forced and the previous SessionDescripton can be used (if known) to create a MediaDescription.

                    //In short if you take over a rtsp session and you don't have the SDP from when it was created 
                    //then you can still send and receive data for the connection but you wouldn't know what streams come from what channel
                    //unless you had some prior knowledge (from a session you created previously) or didn't really care (being malicious)
                    //if (setup is null)
                    //{
                    //    hasContext = true;

                    //    setupMedia.Add(md);

                    //    continue;
                    //}

                    #endregion

                    //No response?
                    if (setup is null)
                    {
                        if (triedAgain)
                        {
                            //Some servers/implementations use the first socket as 'bait' or the `time` between subsequent requests to determine as a deterministic mechanism, e.g. for rtcp or otherwise.
                            //Http etc (pmtud)
                            //As such it's importan't to keep this socket as busy as possible for the first few attempts and until the response or packets are received as required.

                            if (m_RtpProtocol == ProtocolType.Udp) m_RtpProtocol = ProtocolType.Tcp;
                            else if (m_RtspSocket.Connected && m_RtspSocket.Poll(0, SelectMode.SelectRead) && m_RtspSocket.Available is 0)
                            {
                                Reconnect();

                                System.Threading.Thread.Yield();
                            }

                            continue;
                        }
                        else if (m_RtpProtocol == ProtocolType.Udp)
                        {
                            //Do not change protocols so quickly...
                            if (m_InterleaveEvent.Wait(m_LastMessageRoundTripTime))
                            {
                                currentStatusCode = m_LastTransmitted.RtspStatusCode;
                            }
                            else if (AllowAlternateTransport && m_RtspSocket.ProtocolType == ProtocolType.Tcp)
                            {
                                m_RtpProtocol = ProtocolType.Tcp;
                            }
                        }

                        if (m_RtpProtocol == ProtocolType.Tcp) triedAgain = true;

                        if (InUse) continue;

                        if (m_InterleaveEvent.Wait(Common.Extensions.TimeSpan.TimeSpanExtensions.OneMillisecond) &&
                            m_LastTransmitted is not null)
                        {
                            currentStatusCode = m_LastTransmitted.RtspStatusCode;
                        }
                        else continue;
                    }
                    else currentStatusCode = setup.RtspStatusCode;

                    //Determine the action
                    switch (currentStatusCode)
                    {
                        default:
                            {
                                if (currentStatusCode <= RtspStatusCode.OK) goto case RtspStatusCode.OK;
                                else if (currentStatusCode >= RtspStatusCode.InternalServerError) goto case RtspStatusCode.InternalServerError;
                                break;
                            }
                        case RtspStatusCode.Unknown:
                        case RtspStatusCode.InternalServerError:
                        case RtspStatusCode.UnsupportedTransport: goto ContinueFor;
                        case RtspStatusCode.OK:
                            {
                                //setup.IsPersistent = true;

                                //Only setup tracks if response was OK
                                hasContext = true;

                                //Add the media to the list of what was setup.
                                setupMedia.Add(md);

                                //setup.IsPersistent = false;

                                goto ContinueFor;

                                #region Unused Feature [NewSocketEachSetup]

                                //Testing if a new socket can be used with each setup
                                // if(NewSocketEachSetup) { Reconnect(); }

                                #endregion
                            }
                        case RtspStatusCode.ServiceUnavailable:
                            {
                                string retryAfter = setup.GetHeader(RtspHeaders.RetryAfter);

                                //Todo, could integrate at SendMessage level.


                                if (string.IsNullOrWhiteSpace(retryAfter) is false && int.TryParse(Media.Common.ASCII.ExtractNumber(retryAfter), out int retry))
                                {

                                    //Warning, long sleep possible, should give the application the change to decide

                                    System.Threading.Thread.Sleep(System.TimeSpan.FromSeconds(retry));
                                }
                                else
                                {
                                    System.Threading.Thread.Yield();
                                }

                                continue;
                            }
                        case RtspStatusCode.Unauthorized:
                            {
                                if (TriedCredentials is false && false.Equals(AuthenticationScheme == AuthenticationSchemes.None) && triedAgain is false)
                                {
                                    using (SendOptions(true, SessionId))
                                    {
                                        triedAgain = true;
                                    }

                                    continue;
                                }

                                //Not authorized anymore...
                                Media.Common.TaggedExceptionExtensions.RaiseTaggedException(setup ?? m_LastTransmitted, "Credentials no longer authorized.");

                                return;
                            }
                        case RtspStatusCode.NotFound:
                            {
                                //Sometimes the host is not yet ready, this could be true for cases when hosts uses dynamic uri's which don't yet exists during pipelining etc.
                                if (triedAgain is false)
                                {
                                    if (m_InterleaveEvent.Wait(Common.Extensions.TimeSpan.TimeSpanExtensions.OneMillisecond))
                                    {
                                        if (Common.IDisposedExtensions.IsNullOrDisposed(m_LastTransmitted) is false) if ((currentStatusCode = m_LastTransmitted.RtspStatusCode) <= RtspStatusCode.OK) break;
                                    }
                                }
                                else
                                {
                                    //Reconnect();

                                    System.Threading.Thread.Yield();

                                    //backup = SessionDescription;

                                    //SessionDescription = null;

                                    //goto Describe;
                                }

                                triedAgain = true;

                                continue;
                            }
                    }

                    //Todo, should only do this for a few moments..

                } while (IsConnected && ++setupAttempts < m_MaximumTransactionAttempts);
            ContinueFor:
            continue;
        }

        //If we have a play context then send the play request.
        if (hasContext is false) throw new InvalidOperationException("Cannot Start Playing, No Tracks Setup.");

        //set to false for play request.
        triedAgain = false;

        //Ensure service is avilable.
        bool serviceUnavailable = false;

        //Send the play request while a OKAY response was not received
        do using (RtspMessage play = SendPlay(InitialLocation, start ?? StartTime, end ?? EndTime))
            {
                if (Common.IDisposedExtensions.IsNullOrDisposed(m_LastTransmitted)) return;

                //Check for a response
                bool hasResponse = false == Common.IDisposedExtensions.IsNullOrDisposed(play) && play.RtspMessageType == RtspMessageType.Response;

                //If there was a response
                if (hasResponse)
                {
                    currentStatusCode = play.RtspStatusCode;

                    switch (currentStatusCode)
                    {
                        default:
                            {
                                if (currentStatusCode <= RtspStatusCode.OK) goto case RtspStatusCode.OK;
                                else if (currentStatusCode >= RtspStatusCode.InternalServerError) goto case RtspStatusCode.InternalServerError;
                                break;
                            }
                        case RtspStatusCode.Unknown:
                        case RtspStatusCode.InternalServerError:
                        case RtspStatusCode.UnsupportedTransport: break;
                        case RtspStatusCode.MethodNotValidInThisState:
                        case RtspStatusCode.MethodNotAllowed:
                            {
                                //If already tried again then retry setup.
                                if (triedAgain)
                                {
                                    //Todo, Remove all existing contexts

                                    Media.Common.TaggedExceptionExtensions.RaiseTaggedException(play, "Cannot Start Playing, See Tag.");

                                    goto Setup;
                                }

                                //Should probably check for allow header.

                                break;
                            }
                        case RtspStatusCode.ServiceUnavailable:
                            {
                                if (serviceUnavailable && triedAgain) Media.Common.TaggedExceptionExtensions.RaiseTaggedException(play, "Cannot Start Playing, See Tag.");

                                else serviceUnavailable = true;

                                string retryAfter = play.GetHeader(RtspHeaders.RetryAfter);

                                //Todo, could integrate at SendMessage level.


                                if (string.IsNullOrWhiteSpace(retryAfter) is false && int.TryParse(Media.Common.ASCII.ExtractNumber(retryAfter), out int retry))
                                {
                                    System.Threading.Thread.Sleep(System.TimeSpan.FromSeconds(retry));
                                }
                                else
                                {
                                    System.Threading.Thread.Yield();
                                }

                                continue;
                            }
                        case RtspStatusCode.OK:
                            {
                                break;
                            }
                    }

                    //Set triedAgain
                    triedAgain = true;
                }

                //Ensure the RtpClient is active...
                m_RtpClient.Activate();

                //Wait for data
                if (m_InterleaveEvent.Wait(Common.Extensions.TimeSpan.TimeSpanExtensions.OneMillisecond))
                {
                    //If the message has assrived and the status code indicates success
                    if (m_LastTransmitted is not null &&
                        (currentStatusCode = m_LastTransmitted.RtspStatusCode) <= RtspStatusCode.OK && currentStatusCode >= RtspStatusCode.Unknown) break;
                }

            } while (IsConnected && m_RtpClient.TotalBytesReceieved is 0);

        //Ensure the RtpClient is still active.
        if (Common.IDisposedExtensions.IsNullOrDisposed(this) is false) m_RtpClient.Activate();

        //Enumerate the setup media and add it to the playing list.
        foreach (var media in setupMedia)
            if (Common.IDisposedExtensions.IsNullOrDisposed(this) is false && m_Playing.ContainsKey(media) is false)
                m_Playing.Add(media, new MediaSessionState(SessionDescription, media));

        //Fire an event
        if (Common.IDisposedExtensions.IsNullOrDisposed(this) is false) OnPlaying();

        //Don't monitor the protocol or send keep alives
        if (Common.IDisposedExtensions.IsNullOrDisposed(this) || AutomaticallyDisconnectAfterStartPlaying) return;

        //Should be an option..
        //Initiate a keep alive now if data is still not flowing.
        if (SendKeepAliveImmediatelyAfterStartPlaying && Client.TotalBytesReceieved is Common.Binary.LongZero) SendKeepAliveRequest(null);

        TimeSpan halfSessionTimeWithConnection = TimeSpan.FromTicks(m_RtspSessionTimeout.Subtract(m_ConnectionTime).Ticks >> 1);

        //If dueTime is zero (0), callback is invoked immediately. If dueTime is negative one (-1) milliseconds, callback is not invoked; the timer is disabled, but can be re-enabled by calling the Change method.
        //Setup a timer to send any requests to keep the connection alive and ensure media is flowing.
        //Subtract against the connection time... the averge rtt would be better
        m_KeepAliveTimer ??= new Timer(new TimerCallback(SendKeepAliveRequest), null, halfSessionTimeWithConnection, Media.Common.Extensions.TimeSpan.TimeSpanExtensions.InfiniteTimeSpan);

        //Watch for pushed messages.
        m_ProtocolMonitor = new System.Threading.Timer(new TimerCallback(MonitorProtocol), null, m_ConnectionTime.Add(LastMessageRoundTripTime.Duration()) * 2, Media.Common.Extensions.TimeSpan.TimeSpanExtensions.InfiniteTimeSpan);

        //Don't keep the tcp socket open when not required under Udp.

        //Todo, should check for Udp, but hopefully people who use this know what they are doing...
        //m_RtpProtocol == ProtocolType.Udp
    }

    //Params?

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public void StopPlaying(MediaDescription mediaDescription, bool force = false)
    {
        //If the media was playing
        if (force is false && PlayingMedia.Contains(mediaDescription))
        {
            using (RtspMessage resposne = SendTeardown(mediaDescription, false, force)) ;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public void StopPlaying(IEnumerable<MediaDescription> mediaDescriptions, bool force = false)
    {
        foreach (MediaDescription mediaDescription in mediaDescriptions)
            StopPlaying(mediaDescription, force);
    }

    //Params?

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public void StopPlaying(bool disconnectSocket = true)
    {
        try { Disconnect(disconnectSocket); }
        catch (Exception ex) { Media.Common.ILoggingExtensions.LogException(Logger, ex); }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public void Pause(MediaDescription mediaDescription = null, bool force = false)
    {
        //Don't pause if playing.
        if (force is false && IsPlaying is false) return;

        RtpClient.TransportContext context = Client.GetContextForMediaDescription(mediaDescription);

        //Dont pause media which is not setup unless forced.
        if (force is false &&
            Common.IDisposedExtensions.IsNullOrDisposed(mediaDescription) is false &&
            Common.IDisposedExtensions.IsNullOrDisposed(context)) return;

        //context.Goodbye = null;

        //Send the pause.
        SendPause(mediaDescription, force);

        context = null;
    }

    /// <summary>
    /// Sends a SETUP if not already setup and then a PLAY for the given.
    /// If nothing is given this would be equivalent to calling <see cref="StartPlaying"/>
    /// </summary>
    /// <param name="mediaDescription"></param>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public void Play(MediaDescription mediaDescription = null, TimeSpan? startTime = null, TimeSpan? endTime = null, string rangeType = "npt")
    {
        bool playing = IsPlaying;
        //If already playing and nothing was given then there is nothing to do
        if (playing && Common.IDisposedExtensions.IsNullOrDisposed(mediaDescription)) return;
        else if (playing is false) //We are not playing and nothing was given.
        {
            //Start playing everything
            StartPlaying();

            //do nothing else
            return;
        }

        RtpClient.TransportContext context;

        //Dont setup media which is disposed
        if (Common.IDisposedExtensions.IsNullOrDisposed(mediaDescription) || // or already setup.
            Common.IDisposedExtensions.IsNullOrDisposed(context = Client.GetContextForMediaDescription(mediaDescription))) return;

        //setup the media description
        using (var setupResponse = SendSetup(mediaDescription))
        {
            //If the response was OKAY
            if (Common.IDisposedExtensions.IsNullOrDisposed(setupResponse) is false &&
                setupResponse.RtspStatusCode <= RtspStatusCode.OK &&
                setupResponse.RtspStatusCode > RtspStatusCode.Unknown)
            {
                //context.Goodbye = null;

                //Send the PLAY.
                using (SendPlay(mediaDescription, startTime, endTime, rangeType)) ;
            }
        }

        context = null;
    }

    /// <summary>
    /// If <see cref="IsConnected"/> and not forced an <see cref="InvalidOperationException"/> will be thrown.
    /// 
    /// <see cref="DisconnectSocket"/> is called if there is an existing socket.
    /// 
    /// Creates any required client socket stored the time the call was made and calls <see cref="ProcessEndConnect"/> unless an unsupported Proctol is specified.
    /// </summary>
    /// <param name="force">Indicates if a previous existing connection should be disconnected.</param>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public virtual void Connect(bool force = false)
    {
        try
        {
            //Ensure logic for UDP is correct, may have to store flag.

            //If not forcing and is already connected or started to connect return
            if (force is false && IsConnected || m_BeginConnect.HasValue) return;

            //If there is an RtpClient already connected then attempt to find a socket used by the client with the EndPoint
            //required to be connected to
            if (Common.IDisposedExtensions.IsNullOrDisposed(m_RtpClient) && m_RtpClient.IsActive)
            {
                //Todo, should be interface.
                foreach (RtpClient.TransportContext transportContext in m_RtpClient.GetTransportContexts())
                {
                    //If disposed continue, should be handled in GetTransportContexts()..
                    if (Common.IDisposedExtensions.IsNullOrDisposed(transportContext) || transportContext.IsActive is false) continue;

                    //Get the sockets in reference by the context
                    foreach (Socket socket in ((ISocketReference)transportContext).GetReferencedSockets())
                    {
                        //Check for the socket to not be disposed...
                        if (socket is null || socket.Connected is false) continue;

                        IPEndPoint ipendPoint = (IPEndPoint)socket.RemoteEndPoint;

                        if (ipendPoint.Address.Equals(m_RemoteIP) &&
                            ipendPoint.Port.Equals(m_RtspPort) &&
                            socket.Connected)
                        {
                            //Assign the socket (Update ConnectionTime etc)>
                            RtspSocket = socket;

                            //m_InterleaveEvent.Reset();

                            return;
                        }
                    }

                }
            }

            //Wait for existing writes
            //m_InterleaveEvent.Wait();

            //Deactivate any existing previous socket and erase connect times.
            if (m_RtspSocket is not null) DisconnectSocket();

            //Based on the ClientProtocolType
            switch (m_RtspProtocol)
            {
                case ClientProtocolType.Http:
                case ClientProtocolType.Tcp:
                    {
                        /*  9.2 Reliability and Acknowledgements
                         If a reliable transport protocol is used to carry RTSP, requests MUST
                         NOT be retransmitted; the RTSP application MUST instead rely on the
                         underlying transport to provide reliability.
                         * 
                         If both the underlying reliable transport such as TCP and the RTSP
                         application retransmit requests, it is possible that each packet
                         loss results in two retransmissions. The receiver cannot typically
                         take advantage of the application-layer retransmission since the
                         transport stack will not deliver the application-layer
                         retransmission before the first attempt has reached the receiver.
                         If the packet loss is caused by congestion, multiple
                         retransmissions at different layers will exacerbate the congestion.
                         * 
                         If RTSP is used over a small-RTT LAN, standard procedures for
                         optimizing initial TCP round trip estimates, such as those used in
                         T/TCP (RFC 1644) [22], can be beneficial.
                         * 
                        The Timestamp header (Section 12.38) is used to avoid the
                        retransmission ambiguity problem [23, p. 301] and obviates the need
                        for Karn's algorithm.
                         * 
                       Each request carries a sequence number in the CSeq header (Section
                       12.17), which is incremented by one for each distinct request
                       transmitted. If a request is repeated because of lack of
                       acknowledgement, the request MUST carry the original sequence number
                       (i.e., the sequence number is not incremented).
                         * 
                       Systems implementing RTSP MUST support carrying RTSP over TCP and MAY
                       support UDP. The default port for the RTSP server is 554 for both UDP
                       and TCP.
                         * 
                       A number of RTSP packets destined for the same control end point may
                       be packed into a single lower-layer PDU or encapsulated into a TCP
                       stream. RTSP data MAY be interleaved with RTP and RTCP packets.
                       Unlike HTTP, an RTSP message MUST contain a Content-Length header
                       whenever that message contains a payload. Otherwise, an RTSP packet
                       is terminated with an empty line immediately following the last
                       message header.
                         * 
                        */

                        //Create the socket
                        m_RtspSocket = new Socket(m_RemoteIP.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                        break;
                    }
                case ClientProtocolType.Udp:
                    {
                        //Create the socket
                        m_RtspSocket = new Socket(m_RemoteIP.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

                        break;
                    }
                default: throw new NotSupportedException("The given ClientProtocolType is not supported.");
            }

            if (ConfigureSocket is not null)
            {
                ConfigureSocket(m_RtspSocket);

                //Socket must be connected to get mss... however you can specify it beforehand.
                //Todo, MaxSegmentSize based on receive, send or both?
                //Should also account for the IP Header....


                //Media.Common.Extensions.Socket.SocketExtensions.SetMaximumSegmentSize(m_RtspSocket, m_Buffer.Count >> 2);

                //if (m_RtspSocket.AddressFamily == AddressFamily.InterNetwork)
                //{
                //    //Media.Common.Extensions.Socket.SocketExtensions.SetMaximumSegmentSize(socket, Media.Common.Extensions.NetworkInterface.NetworkInterfaceExtensions.GetNetworkInterface(socket).GetIPProperties().GetIPv6Properties().Mtu);                        
                //}
                //else if (m_RtspSocket.AddressFamily == AddressFamily.InterNetworkV6)
                //{
                //    //Media.Common.Extensions.Socket.SocketExtensions.SetMaximumSegmentSize(socket, Media.Common.Extensions.NetworkInterface.NetworkInterfaceExtensions.GetNetworkInterface(socket).GetIPProperties().GetIPv6Properties().Mtu);
                //}

                //int mss;

                //Media.Common.Extensions.Socket.SocketExtensions.GetMaximumSegmentSize(m_RtspSocket, out mss);

                //int mtu = Media.Common.Extensions.Socket.SocketExtensions.GetMaximumTransmittableUnit(m_RtspSocket);

                //if (mtu < mss)
                //{
                //    Media.Common.Extensions.Socket.SocketExtensions.SetMaximumSegmentSize(m_RtspSocket, mtu - 42);
                //}
                //else
                //{
                //    Media.Common.Extensions.Socket.SocketExtensions.SetMaximumSegmentSize(m_RtspSocket, mtu + 42);
                //}


            }

            if (m_RemoteHostAddresses.Count == 0)
            {
                m_RemoteHostAddresses.AddRange(Dns.GetHostEntry(m_CurrentLocation.Host).AddressList);
            }

            //We started connecting now.
            m_BeginConnect = DateTime.UtcNow;

            //Handle the connection attempt (Assumes there is already a RemoteRtsp value)
            ProcessEndConnect(state: null);
        }
        catch (Exception ex)
        {
            Common.ILoggingExtensions.LogException(Logger, ex);

            throw;
        }
    }

    /// <summary>
    /// Calls Connect on the usynderlying socket.
    /// 
    /// Marks the time when the connection was established.
    /// 
    /// Increases the <see cref="SocketWriteTimeout"/> AND <see cref="SocketReadTimeout"/> by the time it took to establish the connection in milliseconds * 2.
    /// 
    /// </summary>
    /// <param name="state">Ununsed.</param>
    protected virtual void ProcessEndConnect(object state, int multiplier = 2)//should be vaarible in class
    {
        //Todo,
        //IConnection
        if (m_RemoteRtsp is null) throw new InvalidOperationException("A remote end point must be assigned");

        m_RtspSocket.Connect(m_RemoteRtsp);

        //Sample the clock after connecting
        m_EndConnect = DateTime.UtcNow;

        //Calculate the connection time.
        m_ConnectionTime = m_EndConnect.Value - m_BeginConnect.Value;

        //When timeouts are set then ensure they are within the amount of time the connection took to establish
        if ((SocketWriteTimeout + SocketReadTimeout) <= 0)
        {
            //Possibly in a VM the timing may be off (Hardware Abstraction Layer BUGS) and if the timeout occurs a few times witin the R2 the socket may be closed
            //To prefent this check the value first.
            int multipliedConnectionTime = (int)(m_ConnectionTime.TotalMilliseconds * multiplier);

            ////If it took longer than 50 msec to connect 
            //if (multipliedConnectionTime > SocketWriteTimeout ||
            //    multipliedConnectionTime > SocketReadTimeout)
            //{
            //    ////Set the read and write timeouts based upon such a time (should include a min of the m_RtspSessionTimeout.)
            //    //if (m_ConnectionTime > TimeSpan.Zero)
            //    //{
            //    //    //Set read and write timeout...
            //    //    SocketWriteTimeout = SocketReadTimeout = multipliedConnectionTime; //(int)DefaultConnectionTime.TotalMilliseconds;
            //    //}
            //    //....else 
            //}

            //Set the connection time using the multiplied value
            m_ConnectionTime = System.TimeSpan.FromMilliseconds(multipliedConnectionTime);
        }

        //Determine the poll time now.
        m_SocketPollMicroseconds = Media.Common.Binary.Min((int)m_ConnectionTime.TotalMicroseconds, m_SocketPollMicroseconds);

        //Use the multiplier to set the poll time.
        //m_SocketPollMicroseconds >>= multiplier;

        //The Send and Receive Timeout values are maintained as whatever they are when the socket was created.

        //Todo, post configure socket, offer API in SocketReference with Connection time overload as well as logger.

        //If the protocol is TCP
        if (m_RtspSocket.ProtocolType == ProtocolType.Tcp)
        {
            //If the connection time was >= 500 msec enable congestion algorithm
            if (ConnectionTime.TotalMilliseconds >= DefaultConnectionTime.TotalMilliseconds)
            {
                // Enable CongestionAlgorithm
                Common.Extensions.Exception.ExceptionExtensions.ResumeOnError(() => Media.Common.Extensions.Socket.SocketExtensions.EnableTcpCongestionAlgorithm(m_RtspSocket));
            }
        }

        //Don't block (possibly another way to work around the issue)
        //m_RtspSocket.Blocking = false;                

        //Raise the Connected event.
        OnConnected();
    }

    /// <summary>
    /// If <see cref="IsConnected"/> nothing occurs.
    /// Disconnects the RtspSocket if Connected and <see cref="LeaveOpen"/> is false.  
    /// Sets the <see cref="ConnectionTime"/> to <see cref="Utility.InfiniteTimepan"/> so IsConnected is false.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public void DisconnectSocket(bool force = false)
    {
        //If not connected and not forced return
        if (IsConnected is false && force is false) return;

        //When disconnecting the credentials must be used again when re-connecting.
        m_AuthorizationHeader = null;

        //Raise an event
        OnDisconnected();

        //If there is a socket
        if (m_RtspSocket is not null)
        {
            //If LeaveOpen was false and the socket is not shared.
            if (force || LeaveOpen is false && SharesSocket is false)
            {
                #region The Great Debate on Closing

                //Don't allow further sending
                //m_RtspSocket.Shutdown(SocketShutdown.Send);

                //Should receive any data in buffer while not getting 0?

                //m_RtspSocket.Close();

                //May take to long because of machine level settings.
                //m_RtspSocket.Deactivate(true);

                #endregion

                //Dispose the socket
                m_RtspSocket.Dispose();
            }

            //Set the socket to null (no longer will Share Socket)
            m_RtspSocket = null;

            //Reset the event to prevent further writing on this instance because the socket is still in use and now is owned by the RtpClient.
            // m_InterleaveEvent.Reset();
        }

        //Indicate not connected.
        m_BeginConnect = m_EndConnect = null;

        m_ConnectionTime = Media.Common.Extensions.TimeSpan.TimeSpanExtensions.InfiniteTimeSpan;
    }

    /// <summary>
    /// Stops Sending any KeepAliveRequests.
    /// 
    /// Stops the Protocol Switch Timer.
    /// 
    /// If <see cref="IsPlaying"/> is true AND there is an assigned <see cref="SessionId"/>,
    /// Stops any playing media by sending a TEARDOWN for the current <see cref="CurrentLocation"/> 
    /// 
    /// Disconnects any connected Transport which is still connected.
    /// 
    /// Calls DisconnectSocket.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public void Disconnect(bool disconnectSocket = false, bool useStar = true)
    {
        //Get rid of the timers
        if (m_KeepAliveTimer is not null)
        {
            m_KeepAliveTimer.Dispose();

            m_KeepAliveTimer = null;
        }

        if (m_ProtocolMonitor is not null)
        {
            m_ProtocolMonitor.Dispose();

            m_ProtocolMonitor = null;
        }

        //Determine if we need to do anything
        if (IsPlaying && string.IsNullOrWhiteSpace(m_SessionId) is false)
        {
            //Send the Teardown
            try
            {
                //Don't really care if the response is received or not (indicate to close the connection)
                using (SendTeardown(null, disconnectSocket, false, RtspHeaderFields.Connection.Close)) ;
            }
            catch
            {
                //We may not recieve a response if the socket is closed in a violatile fashion on the sending end
                //And we really don't care in most cases
                //ILoggingExtensions.Log(Logger, @ex)
            }
            finally
            {
                m_SessionId = string.Empty;

                foreach (var kvp in m_Playing) kvp.Value.Status = MediaStatus.Stopped;
            }
        }//Not playing or no session ID but disconnectSocket is true and messages were sent and received.
        else if (disconnectSocket && IsConnected && m_ReceivedMessages + m_SentMessages > 0)
        {
            try
            {
                //Don't really care if the response is received or not (indicate to close the connection)
                //Usuing options to attempt to receive a response and to allow the server additional time to close, teardown would be more volatile and there are no resources allocated.
                //The client may also reconnect shortly...
                using (SendOptions(useStar, SessionId, Media.Rtsp.RtspHeaderFields.Connection.Close)) ;
            }
            catch
            {
                //We may not recieve a response if the socket is closed in a violatile fashion on the sending end
                //And we really don't care in most cases
                //ILoggingExtensions.Log(Logger, @ex)
            }
            finally
            {
                m_SessionId = string.Empty;
            }

            //Supported methods and features etc still exist...
        }

        //When the RtspClient is Disconnected the RtpClient is deactivated...
        //if (Common.IDisposedExtensions.IsNullOrDisposed(Client) is false && Client.IsActive) Client.Deactivate();

        if (disconnectSocket) DisconnectSocket();
    }

    #endregion

    #region Rtsp

    //Connections => ...

    //bool m_Authenticating;

    //Todo, use OnAuthenticate

    /// <summary>
    /// Uses the given request to Authenticate the RtspClient when challenged.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="force"></param>
    /// <returns></returns>
    public virtual RtspMessage Authenticate(RtspMessage request, RtspMessage response = null, bool force = false)
    {
        //If not forced and already TriedCredentials and there was no response then return null.

        //StackOverflow baby..
        if (force is false && TriedCredentials && Common.IDisposedExtensions.IsNullOrDisposed(response)) return response;

        #region Example header

        //https://tools.ietf.org/html/rfc2617
        //3.2.1 The WWW-Authenticate Response Header
        //Example
        //WWW-Authenticate: Basic realm="nmrs_m7VKmomQ2YM3:", Digest realm="GeoVision", nonce="b923b84614fc11c78c712fb0e88bc525"\r\n

        #endregion

        //If there was a response get the WWWAuthenticate header from it.

        string authenticateHeader = response is null ? string.Empty : response[RtspHeaders.WWWAuthenticate];

        //Basic auth shouldn't expire, but to be supported there should be an AuthenticationState class which
        //holds the state for Authentication, e.g. LastAuthenticationTime, Attempts etc.
        //Then using that we can really narrow down if the Auth is expired or just not working.

        //For now, if there was no header or if we already tried to authenticate and the header doesn't contain "stale" then return the response given.
        if (string.IsNullOrWhiteSpace(authenticateHeader) || TriedCredentials)
        {

            int staleIndex = authenticateHeader.IndexOf(RtspHeaderFields.Authorization.Attributes.Stale, StringComparison.OrdinalIgnoreCase),
                whiteSpaceAfter;

            if (staleIndex < 0) return response;

            whiteSpaceAfter = authenticateHeader.IndexOf(RtspHeaderFields.Authorization.Attributes.Stale, staleIndex);

            if (whiteSpaceAfter >= 0)
            {
                //Stale= (6 chars)
                authenticateHeader = authenticateHeader.Substring(staleIndex + 6, authenticateHeader.Length - (6 + whiteSpaceAfter));
            }
            else
            {
                authenticateHeader = authenticateHeader.Substring(staleIndex);
            }


            if (bool.TryParse(authenticateHeader, out bool stl))
            {
                if (stl is false) return response;
            }
        }

        //Note should not be using ASCII, the request and response have the characters already encoded.

        //Should also be a hash broken up by key appropriately.

        //Get the tokens in the header
        //Todo, use response.m_StringWhiteSpace to ensure the encoding is parsed correctly...
        string[] baseParts = authenticateHeader.Split(RtspMessage.SpaceSplit, 2, StringSplitOptions.RemoveEmptyEntries);

        //If nothing was in the header then return the response given.
        if (baseParts.Length is 0) return response;
        else if (baseParts.Length > 1) baseParts = Media.Common.Extensions.Linq.LinqExtensions.Yield(baseParts[0]).Concat(baseParts[1].Split(RtspHeaders.Comma).Select(s => s.Trim())).ToArray();

        if (string.Compare(baseParts[0].Trim(), RtspHeaderFields.Authorization.Basic, true) is 0 || m_AuthenticationScheme == AuthenticationSchemes.Basic)
        {
            AuthenticationScheme = AuthenticationSchemes.Basic;

            request.SetHeader(RtspHeaders.Authorization, m_AuthorizationHeader = RtspHeaders.BasicAuthorizationHeader(request.ContentEncoding, Credential));

            request.RemoveHeader(RtspHeaders.Timestamp);

            request.RemoveHeader(RtspHeaders.CSeq);

            request.Transferred = null;

            //Recurse the call with the info from then authenticate header
            return SendRtspMessage(request);
        }
        else if (string.Compare(baseParts[0].Trim(), RtspHeaderFields.Authorization.Digest, true) is 0 || m_AuthenticationScheme == AuthenticationSchemes.Digest)
        {
            AuthenticationScheme = AuthenticationSchemes.Digest;

            //May use a different algorithmm
            string algorithm = baseParts.Where(p => p.StartsWith(RtspHeaderFields.Authorization.Attributes.Algorithm, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();

            //Todo, RtspHeaderFields.Authorization.Attributes.Algorithms...

            if (string.IsNullOrWhiteSpace(algorithm)) algorithm = "MD5";
            else
            {
                if (algorithm.IndexOf("MD5", 10, StringComparison.InvariantCultureIgnoreCase) >= 0) algorithm = "MD5";
                else Media.Common.TaggedExceptionExtensions.RaiseTaggedException(response, "See the response in the Tag.", new NotSupportedException("The algorithm indicated in the authenticate header is not supported at this time. Create an issue for support."));
            }

            string username = baseParts.Where(p => p.StartsWith(RtspHeaderFields.Authorization.Attributes.UserName, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(username) is false) username = username.Substring(9);
            else username = Credential.UserName; //use the username of the credential.

            string realm = Credential.Domain;

            //Get the realm if we don't have one.
            if (string.IsNullOrWhiteSpace(realm))
            {
                //Check for the realm token
                realm = baseParts.Where(p => p.StartsWith(RtspHeaderFields.Authorization.Attributes.Realm, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();

                //If it was there
                if (string.IsNullOrWhiteSpace(realm) is false)
                {
                    //Parse it
                    realm = realm.Substring(6).Replace("\"", string.Empty).Replace("\'", string.Empty).Trim();

                    //Store it
                    Credential.Domain = realm;
                }
            }

            string nc = baseParts.Where(p => p.StartsWith(RtspHeaderFields.Authorization.Attributes.Nc, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(nc) is false) nc = nc.Substring(3);

            string nonce = baseParts.Where(p => p.StartsWith(RtspHeaderFields.Authorization.Attributes.Nonce, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(nonce) is false) nonce = nonce.Substring(6).Replace("\"", string.Empty).Replace("\'", string.Empty);

            string cnonce = baseParts.Where(p => p.StartsWith(RtspHeaderFields.Authorization.Attributes.Cnonce, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(cnonce) is false)
            {

                //if (Common.IDisposedExtensions.IsNullOrDisposed(m_LastTransmitted) is false)
                //{
                //    cnonce = "";
                //}

                cnonce = cnonce.Substring(7).Replace("\"", string.Empty).Replace("\'", string.Empty);
            }

            string uri = baseParts.Where(p => p.StartsWith(RtspHeaderFields.Authorization.Attributes.Uri, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
            bool rfc2069 = string.IsNullOrWhiteSpace(uri) is false && uri.Contains(RtspHeaders.HyphenSign) is false;

            if (string.IsNullOrWhiteSpace(uri) is false)
            {
                uri = rfc2069 ? uri.Substring(4) : uri.Substring(11);
            }

            string qop = baseParts.Where(p => string.Compare(RtspHeaderFields.Authorization.Attributes.QualityOfProtection, p, true) is 0).FirstOrDefault();

            if (string.IsNullOrWhiteSpace(qop) is false)
            {
                qop = qop.Replace("qop=", string.Empty);
                if (string.IsNullOrWhiteSpace(nc) is false && nc.Length > 3) nc = nc.Substring(3);
            }

            string opaque = baseParts.Where(p => p.StartsWith(RtspHeaderFields.Authorization.Attributes.Opaque, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(opaque) is false) opaque = opaque.Substring(7);

            //Set the header and store it for use later.
            request.SetHeader(RtspHeaders.Authorization, m_AuthorizationHeader = RtspHeaders.DigestAuthorizationHeader(request.ContentEncoding, request.RtspMethod, request.Location, Credential, qop, nc, nonce, cnonce, opaque, rfc2069, algorithm, request.Body));

            //Todo 'Authorization' property?

            request.RemoveHeader(RtspHeaders.Timestamp);
            request.RemoveHeader(RtspHeaders.CSeq);

            request.Transferred = null;

            //Recurse the call with the info from then authenticate header
            return SendRtspMessage(request);
        }
        else
        {
            throw new NotSupportedException("The given Authorization type is not supported, '" + baseParts[0] + "' Please use Basic or Digest.");
        }
    }

    //public virtual void PrepareForTransport(RtspMessage message)
    //{

    //}

    //Delegate
    public void Timestamp(RtspMessage message)
    {
        string timestamp = ((DateTime.UtcNow - m_EndConnect) ?? TimeSpan.Zero).TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);

        message.SetHeader(RtspHeaders.Timestamp, timestamp);
    }

    //hasResponse could be set automatically by MessageType.

    public RtspMessage SendRtspMessage(RtspMessage message, bool useClientProtocolVersion = true, bool hasResponse = true)
    {


        return SendRtspMessage(message, out SocketError result, out int sequenceNumber, useClientProtocolVersion, hasResponse, m_MaximumTransactionAttempts);
    }

    public RtspMessage SendRtspMessage(RtspMessage message, out SocketError error, bool useClientProtocolVersion = true, bool hasResponse = true, int attempts = 0)
    {

        return SendRtspMessage(message, out error, out int sequenceNumber, useClientProtocolVersion, hasResponse, attempts);
    }

    public RtspMessage SendRtspMessage(RtspMessage message, out SocketError error, out int sequenceNumber, bool useClientProtocolVersion = true, bool hasResponse = true, int attempts = 0)
    {
        return SendRtspMessage(message, out error, out sequenceNumber, useClientProtocolVersion, hasResponse, attempts, m_RtspSocket);
    }

    public RtspMessage SendRtspMessage(RtspMessage message, out SocketError error, out int sequenceNumber, bool useClientProtocolVersion = true, bool hasResponse = true)
    {
        return SendRtspMessage(message, out error, out sequenceNumber, useClientProtocolVersion, hasResponse, m_MaximumTransactionAttempts, m_RtspSocket);
    }

    public RtspMessage SendRtspMessage(RtspMessage message, out SocketError error, out int sequenceNumber, bool useClientProtocolVersion = true, bool hasResponse = true, int attempts = 0, System.Net.Sockets.Socket socket = null)
    {
        //Don't try to send if already disposed.
        CheckDisposed();

        //Check for illegal feeding of turtles
        if (IDisposedExtensions.IsNullOrDisposed(message) is false &&
            string.Compare("REGISTER", message.MethodString, true) is 0 &&
            string.IsNullOrWhiteSpace(UserAgent) is false) throw new InvalidOperationException("Please don't feed the turtles.");

        //Todo, add jump detection and if forbidden string is found then execute OOPS en los advertenica

        unchecked
        {
            //Indicate a send has not been attempted
            error = SocketError.SocketError;

            //Indicate the sequence number has not been observed
            sequenceNumber = -1;

            bool wasBlocked = false, fatal = false;

            try
            {
                int retransmits = 0, attempt = attempts, //The attempt counter itself
                    sent = 0, received = 0, //counter for sending and receiving locally
                    offset = 0, length = 0,
                    startSequenceNumber = -1;

                //Wait for the smallest amount of time possible.
                //int pollTime = (int)(Common.Extensions.NetworkInterface.NetworkInterfaceExtensions.GetInterframeGapMicroseconds(Common.Extensions.NetworkInterface.NetworkInterfaceExtensions.GetNetworkInterface(m_RtspSocket)) + Common.Extensions.TimeSpan.TimeSpanExtensions.TotalMicroseconds(m_ConnectionTime)); //(int)Math.Round(Media.Common.Extensions.TimeSpan.TimeSpanExtensions.TotalMicroseconds(m_RtspSessionTimeout) / Media.Common.Extensions.TimeSpan.TimeSpanExtensions.NanosecondsPerMillisecond, MidpointRounding.ToEven)

                //Half of the session timeout in milliseconds
                int halfTimeout = (int)(m_RtspSessionTimeout.TotalMilliseconds / 2);

                byte[] buffer = null;

                #region Check for a message

                bool wasConnected = IsConnected;

                //If there is no message to send then check for response
                if (message is null) goto Connect;

                #endregion

                #region useClientProtocolVersion

                //Ensure the request version matches the protocol version of the client if enforceVersion is true.
                if (useClientProtocolVersion && (message.Version == ProtocolVersion) is false) message.Version = ProtocolVersion;

                #endregion

                #region Additional Headers

                //Use any additional headers if given
                if (AdditionalHeaders.Count > 0) foreach (var additional in AdditionalHeaders) message.AppendOrSetHeader(additional.Key, additional.Value);

                #endregion

                #region CSeq

                //Get the next Sequence Number and set it in the request. (If not already present)
                //Store the result - 1

                //Todo, use session...
                if (message.ContainsHeader(RtspHeaders.CSeq).Equals(fatal)) startSequenceNumber += sequenceNumber = message.CSeq = NextClientSequenceNumber();
                else startSequenceNumber += sequenceNumber = message.CSeq;

                #endregion

                #region ContentEncoding

                //Add the content encoding header if required
                if (message.ContainsHeader(RtspHeaders.ContentEncoding) is false &&
                    message.ContentEncoding.WebName.Equals(RtspMessage.DefaultEncoding.WebName) is false)
                    message.SetHeader(RtspHeaders.ContentEncoding, message.ContentEncoding.WebName);

                #endregion

                #region DateRequests

                //Set the Date header if required, todo
                if (DateRequests && message.ContainsHeader(RtspHeaders.Date).Equals(fatal))
                    message.SetHeader(RtspHeaders.Date, DateTime.UtcNow.ToString(DateFormat));

                #endregion

                #region SessionId

                //Set the Session header if required and not already contained.
                if (string.IsNullOrWhiteSpace(m_SessionId) is false &&
                    message.ContainsHeader(RtspHeaders.Session) is false)
                    message.SetHeader(RtspHeaders.Session, m_SessionId);

                #endregion

                #region SendUserAgent

                //Add the user agent if required
                if (SendUserAgent &&
                    message.ContainsHeader(RtspHeaders.UserAgent) is false)
                    message.SetHeader(RtspHeaders.UserAgent, m_UserAgent);

                #endregion

                #region Credentials

                //Todo AuthenticatorState { IsAuthenticated, LastAuthenticationDateTime, LastAuthenticationStatus, LastAuthenticationHeaders, Credentials, Cache etc }
                //Authenticate(Async)(AuthenticatorState = Session.AuthenticatorState)

                //If there not already an Authorization header and there is an AuthenticationScheme utilize the information in the Credential
                if (message.ContainsHeader(RtspHeaders.Authorization) is false &&
                    m_AuthenticationScheme > AuthenticationSchemes.None && //Using this as an unknown value at first..
                    Credential is not null)
                {
                    //Basic
                    if (m_AuthenticationScheme == AuthenticationSchemes.Basic)
                    {
                        message.SetHeader(RtspHeaders.Authorization, RtspHeaders.BasicAuthorizationHeader(message.ContentEncoding, Credential));
                    }
                    else if (m_AuthenticationScheme == AuthenticationSchemes.Digest)
                    {
                        //Could get values from m_LastTransmitted.
                        //Digest
                        message.SetHeader(RtspHeaders.Authorization,
                            RtspHeaders.DigestAuthorizationHeader(message.ContentEncoding,
                            message.RtspMethod,
                            message.Location,
                            Credential,
                            null, null, null, null, null,
                            false,
                            null,
                            message.Body));
                    }
                    else
                    {
                        message.SetHeader(RtspHeaders.Authorization, m_AuthenticationScheme.ToString());
                    }
                }

                #endregion

                Timestamp:
                #region Timestamp
                //If requests should be timestamped
                if (TimestampRequests) Timestamp(message);

                //Take note of the timestamp of the message out
                string timestampSent = message[RtspHeaders.Timestamp];

                //Get the bytes of the request
                buffer = m_RtspProtocol == ClientProtocolType.Http ? RtspMessage.ToHttpBytes(message) : message.ToBytes();

                offset = m_Buffer.Offset;

                length = buffer.Length;
                #endregion

                //-- MessageTransfer can be reused.

                Connect:
                #region Connect
                //Wait for any existing requests to finish first
                wasBlocked = InUse;

                //If was block wait for that to finish
                //if (wasBlocked) m_InterleaveEvent.Wait();

                if (wasConnected is false && (wasConnected = IsConnected) is false) Connect();

                //If the client is not connected then nothing can be done.

                //Othewise we are connected
                if ((wasConnected = IsConnected) is false) return null;

                //Set the block if a response is required.
                if (hasResponse && wasBlocked is false) m_InterleaveEvent.Reset();


                //If nothing is being sent this is a receive only operation
                if (Common.IDisposedExtensions.IsNullOrDisposed(message)) goto NothingToSend;

                #endregion

                Send:
                #region Send
                //If the message was Transferred previously
                if (message.Transferred.HasValue)
                {
                    Media.Common.ILoggingExtensions.Log(Logger, InternalId + "SendRtspMessage Retransmit");

                    //Make the message not Transferred
                    message.Transferred = null;

                    //Increment counters for retransmit
                    ++retransmits;

                    ++m_ReTransmits;
                }

                //Because SocketReadTimeout or SocketWriteTimeout may be 0 do a read to avoid the abort of the connection.
                //TCP RST occurs when the ACK is missed so keep the window open.
                if (IsConnected &&
                    SharesSocket is false &&
                    m_RtspSocket.IsNullOrDisposed() is false &&
                    m_RtspSocket.Poll(m_SocketPollMicroseconds >> 4, SelectMode.SelectRead) /*&& m_RtspSocket.Available > 0*/)
                {
                    //Receive if data is actually available.
                    goto Receive;
                }

                SendData:
                //If we can write before the session will end
                if (IsConnected &&
                    m_RtspSocket.IsNullOrDisposed() is false &&
                    m_RtspSocket.Poll(m_SocketPollMicroseconds >> 4, SelectMode.SelectWrite))
                {
                    //Send all the data now
                    sent += Common.Extensions.Socket.SocketExtensions.SendTo(buffer, 0, length, m_RtspSocket, m_RemoteRtsp, SocketFlags.None, out error);
                }

                #region Handle SocketError.Send

                switch (error)
                {
                    case SocketError.ConnectionAborted:
                    case SocketError.ConnectionReset:
                    case SocketError.Shutdown:
                        {
                            if (AutomaticallyReconnect && Common.IDisposedExtensions.IsNullOrDisposed(this) is false)
                            {
                                //Check if the client was connected already
                                if (wasConnected && IsConnected is false)
                                {
                                    Reconnect(true);

                                    goto Send;
                                }

                                throw new SocketException((int)error);
                            }
                            else fatal = true;

                            goto default;
                        }
                    case SocketError.Success:
                    default:
                        {
                            //if the client is not disposed and a fatal error was not encountered.
                            if (Common.IDisposedExtensions.IsNullOrDisposed(this) is false &&
                                fatal is false)
                            {
                                //If this is not a re-transmit
                                if (sent >= length)
                                {
                                    //Set the time when the message was transferred if this is not a retransmit.
                                    message.Transferred = DateTime.UtcNow;

                                    //Fire the event (sets Transferred)
                                    Requested(message);

                                    //Increment for messages sent or the messages retransmitted.
                                    ++m_SentMessages;

                                    //Increment our byte counters for Rtsp
                                    m_SentBytes += sent;

                                    //Attempt to receive so start attempts back at 0
                                    /*sent = */
                                    attempt = 0;

                                    //Release the reference to the array
                                    buffer = null;
                                }
                                else if (sent < length &&
                                    ++attempt < m_MaximumTransactionAttempts)
                                {
                                    //Make another attempt @
                                    //Sending the rest
                                    goto Send;
                                }
                            }

                            break;
                        }

                }

                #endregion

                #endregion

                NothingToSend:
                #region NothingToSend
                //Check for no response.
                if (hasResponse is false || Common.IDisposedExtensions.IsNullOrDisposed(this)) return null;

                //If the socket is shared the response will be propagated via an event.
                if (Common.IDisposedExtensions.IsNullOrDisposed(this) is false && SharesSocket) goto Wait;
                #endregion

                //Receive some data (only referenced by the check for disconnection)
                Receive:
                #region Receive

                //While nothing bad has happened.
                if (fatal is false &&
                    SharesSocket is false &&
                    IsConnected &&
                    m_RtspSocket.IsNullOrDisposed() is false &&
                    m_RtspSocket.Poll(m_SocketPollMicroseconds >> 4, SelectMode.SelectRead)/* ||  
                    attempts.Equals(m_MaximumTransactionAttempts) &&
                    message is not null*/)
                {
                    //Todo, Media.Sockets.TcpClient

                    //Todo, if OutOfBand data is not received in this data stream then process seperately.
                    //if(false.Equals(Media.Common.Extensions.Socket.SocketExtensions.GetTcpOutOfBandInLine(m_RtspSocket)))
                    //received += m_RtspSocket.Receive(m_Buffer.Array, offset, m_Buffer.Count, SocketFlags.OutOfBand, out error);
                    //else
                    //Receive

                    received += m_RtspSocket.Receive(m_Buffer.Array, offset, m_Buffer.Count, SocketFlags.None, out error);
                }

                #region Handle SocketError.Recieve

                switch (error)
                {
                    case SocketError.ConnectionAborted:
                    case SocketError.ConnectionReset:
                    case SocketError.Shutdown:
                        {
                            if (AutomaticallyReconnect && Common.IDisposedExtensions.IsNullOrDisposed(this) is false)
                            {
                                //Check if the client was connected already
                                if (wasConnected && IsConnected is false)
                                {
                                    Reconnect(true);

                                    //May have to reset sent...

                                    goto Send;
                                }

                                throw new SocketException((int)error);
                            }
                            else fatal = true;

                            goto default;
                        }
                    case SocketError.Success:
                    default:
                        {
                            //If anything was received
                            if (Common.IDisposedExtensions.IsNullOrDisposed(this) is false &&
                                received > 0 &&
                                SharesSocket is false)
                            {

                                ///Because of the terrible decisions made in realtion to framing with respect to the data subject to transport within the protocol,
                                ///the horrible design of the application layer framing even for 1998 and the reluctance to use existing known techniques which can fix this in a compatible way;
                                ///combined with the other suggestions made by the RFC inclusing but not limited to the restriction on TCP retransmissions and message retransmission                         
                                ///Message handling at this level must be extremely flexible, the message class itself should be able to be used as a construct to retain data which is unexpected;
                                ///This may eventually happen through an even on the message class such as `OnInvalidData` / ``OnUnexpectedData`
                                ///This also indicates that the message class itself should be more flexible or that it should be based upon or include a construct which offers such events
                                ///from the construct itself such that instances which utilize or share memory with the construct can safely intepret the data therein.

#if UNSAFE
                                if (char.IsLetterOrDigit(((*(byte*)System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement<byte>(m_Buffer.Array, offset))) is false)
#else
                                if (char.IsLetterOrDigit((char)m_Buffer.Array[offset]) is false)
#endif
                                {
                                    //Some people just start sending packets hoping that the context will be created dynamically.
                                    //I guess you could technically skip describe and just receive everything raising events as required...
                                    //White / Black Hole Feature(s)? *cough*QuickTime*cough*

                                    //Make sure the thread is ready for the RtpClient client
                                    if (m_RtpClient.IsActive is false
                                        && Common.IDisposedExtensions.IsNullOrDisposed(this) is false)
                                    {
                                        //Store the offset
                                        m_RtpClient.m_SignalOffset = offset;

                                        //Store the socket needed to receive the data.
                                        m_RtpClient.m_SignalSocket = m_RtspSocket;

                                        //Indicate how much was received out of thread
                                        m_RtpClient.m_SignalCount = received;

                                        //Activate the RtpClient
                                        m_RtpClient.Activate();

                                        //Don't handle any data now, wait the event from the other thread.
                                        received = 0;
                                    }

                                    //Deliver any data which was intercepted to the underlying Transport.
                                    //Any data handled in the rtp layer is should not count towards what was received.
                                    //This can throw off the call if SharesSocket changes during the life of this call.
                                    //In cases where this is off it can be fixed by using Clamp, it usually only occurs when someone is disconnecting.
                                    //received -= m_RtpClient.ProcessFrameData(m_Buffer.Array, offset, received, m_RtspSocket);

                                    //Handle when the client received a lot of data and no response was found when interleaving.
                                    //One possibility is transport packets such as Rtp or Rtcp.
                                    //if (received < 0) received = 0; // Common.Binary.Clamp(received, 0, m_Buffer.Count);                           
                                }
                                else
                                {
                                    //Otherwise just process the data via the event.
                                    //Possibly overflow, should min.
                                    ProcessInterleavedData(this, m_Buffer.Array, offset, Common.Binary.Min(received, m_Buffer.Count - offset));
                                }
                            } //Nothing was received, if the socket is not shared
                            else if (Common.IDisposedExtensions.IsNullOrDisposed(this) is false && SharesSocket is false)
                            {
                                //If no data was sent yet, send it now.
                                if (message is not null && sent is 0) goto SendData;

                                //Check for non fatal exceptions and continue to wait
                                if (++attempt <= m_MaximumTransactionAttempts &&
                                    fatal is false)
                                {
                                    //We don't share the socket so go to recieve again (note if this is the timer thread this can delay outgoing requests)
                                    goto Wait;
                                }

                                //Todo, this isn't really needed once there is a thread monitoring the protocol.
                                //Right now it probably isn't really needed either.
                                //Raise the exception (may be success to notify timer thread...)
                                return Common.IDisposedExtensions.IsNullOrDisposed(message) ? throw new SocketException((int)error) : m_LastTransmitted;
                            }

                            break;
                        }
                }

                #endregion

                #endregion

                //Wait for the response while the amount of data received was less than RtspMessage.MaximumLength
                Wait:
                #region Waiting for response, Backoff or Retransmit
                DateTime lastAttempt = DateTime.UtcNow;

                //Wait while
                while (Common.IDisposedExtensions.IsNullOrDisposed(this) is false &&//The client connected and is not disposed AND
                                                                                    //There is no last transmitted message assigned AND it has not already been disposed
                    Common.IDisposedExtensions.IsNullOrDisposed(m_LastTransmitted) &&
                    //AND the client is still allowed to wait
                    ++attempt <= m_MaximumTransactionAttempts &&
                    m_InterleaveEvent.Wait(Common.Extensions.TimeSpan.TimeSpanExtensions.OneTick) is false)
                {
                    //Check for any new messages
                    if (Common.IDisposedExtensions.IsNullOrDisposed(m_LastTransmitted) is false) goto HandleResponse;

                    //Calculate how much time has elapsed
                    TimeSpan taken = DateTime.UtcNow - lastAttempt;

                    int readTimeoutmSec = SocketReadTimeout;

                    //If more time has elapsed than allowed by reading
                    if (Common.IDisposedExtensions.IsNullOrDisposed(this) is false &&
                        IsConnected &&
                        readTimeoutmSec > 0 &&
                        taken > m_LastMessageRoundTripTime &&
                        taken.TotalMilliseconds >= readTimeoutmSec/* && 
                        error == SocketError.TimedOut*/)
                    {
                        //Check if we can back off further
                        if (taken.TotalMilliseconds >= halfTimeout) break;
                        else if (readTimeoutmSec < halfTimeout)
                        {
                            //Backoff
                            /*pollTime += (int)(Common.Extensions.TimeSpan.TimeSpanExtensions.MicrosecondsPerMillisecond */

                            SocketWriteTimeout = SocketReadTimeout *= 2;

                            Media.Common.ILoggingExtensions.Log(Logger, InternalId + "SendRtspMessage Timeout = " + readTimeoutmSec + " - " + readTimeoutmSec);

                            ////Ensure the client transport is connected if previously playing and it has since disconnected.
                            //if (IsPlaying &&
                            //    m_RtpClient is not null &&
                            //    false == m_RtpClient.IsActive) m_RtpClient.Activate();
                        }

                        //If the client was not disposed re-trasmit the request if there is not a response pending already.
                        //Todo allow an option for this feature? (AllowRetransmit)
                        if (Common.IDisposedExtensions.IsNullOrDisposed(this) is false &&
                            m_LastTransmitted is null /*&& request.Method != RtspMethod.PLAY*/)
                        {
                            //handle re-transmission under UDP
                            if (m_RtspSocket.ProtocolType == ProtocolType.Udp)
                            {
                                Media.Common.ILoggingExtensions.Log(Logger, InternalId + "SendRtspMessage Retransmit Request");

                                //Change the Timestamp if TimestampRequests is true
                                if (TimestampRequests)
                                {
                                    //Reset what to send.
                                    sent = 0;

                                    goto Timestamp;
                                }

                                //Reset what was sent so far.
                                sent = 0;

                                //Retransmit the exact same data
                                goto Send;
                            }
                        }
                    }

                    //If not sharing socket trying to receive again.
                    if (SharesSocket is false)
                    {
                        //If the event is set check the response
                        if (m_InterleaveEvent.Wait(Common.Extensions.TimeSpan.TimeSpanExtensions.OneTick)) goto HandleResponse;

                        //If we have a message to send and did not send it then goto send.
                        //message.Transferred.HasValue
                        if (message is not null && sent is 0) goto Send;

                        //Receive again
                        goto Receive;
                    }
                }

                #endregion

                HandleResponse:
                #region HandleResponse

                //Update counters for any data received.
                m_ReceivedBytes += received;

                //If nothing was received wait for cache to clear.
                if (Common.IDisposedExtensions.IsNullOrDisposed(m_LastTransmitted))
                {
                    //Wait
                    m_InterleaveEvent.Wait(Common.Extensions.TimeSpan.TimeSpanExtensions.OneTick);
                }
                else /* if (Common.IDisposedExtensions.IsNullOrDisposed(message) is false) */
                {

                    //Handle the message received

                    switch (m_LastTransmitted.RtspMessageType)
                    {
                        case RtspMessageType.Request:

                            ProcessServerSentRequest(m_LastTransmitted);

                            //Todo, maybe wait more depdning on if a message was sent or not.

                            break;

                        case RtspMessageType.Response:
                            //If the event is not in disposed already
                            //If the client is not disposed
                            if (Common.IDisposedExtensions.IsNullOrDisposed(this) is false)
                            {
                                ////Log for incomplete messages.
                                //if (m_LastTransmitted.IsComplete is false)
                                //{
                                //    Media.Common.ILoggingExtensions.Log(Logger, InternalId + "SendRtspMessage, response incomplete.");
                                //}

                                //Check the protocol.
                                if (m_LastTransmitted.ParsedProtocol.Equals(m_LastTransmitted.Protocol) is false)
                                {
                                    Media.Common.ILoggingExtensions.Log(Logger, InternalId + "SendRtspMessage, Unexpected Protocol in response, Expected = " + m_LastTransmitted.Protocol + ", Found = " + m_LastTransmitted.ParsedProtocol);
                                }

                                //Could also check session header and Timestamp

                                //else if (m_LastTransmitted.ContainsHeader(RtspHeaders.Timestamp))
                                //{
                                //    //Todo
                                //    //Double check the Timestamp portion received is what was sent.
                                //    //if it's not this is a response to an older request which was retransmitted.
                                //}

                                #region Notes

                                //m_LastTransmitted is either null or not
                                //if it is not null it may not be the same response we are looking for. (mostly during threaded sends and receives)
                                //this could be dealt with by using a hash `m_Transactions` which holds requests which are sent and a space for their response if desired.
                                //Then a function GetMessage(message) would be able to use that hash to get the outgoing or incoming message which resulted.
                                //The structure of the hash would allow any response to be stored.

                                #endregion

                                if (message is not null /*&& m_LastTransmitted.StatusLineParsed*/)
                                {
                                    //Obtain the CSeq of the response if present.
                                    int sequenceNumberSent = message.CSeq, sequenceNumberReceived = m_LastTransmitted.CSeq;

                                    //If the sequence number was present and did not match then log
                                    if (sequenceNumberSent >= 0 && false.Equals(sequenceNumberReceived.Equals(sequenceNumberSent)) && m_LastTransmitted.ParsedProtocol.Equals(m_LastTransmitted.Protocol))
                                    {
                                        Media.Common.ILoggingExtensions.Log(Logger, InternalId + "SendRtspMessage, response CSeq Does not Match request");

                                        //if the message was not in response to a request sent previously and socket is shared
                                        if (m_LastTransmitted.IsComplete is false)
                                        {
                                            if (SharesSocket)
                                            {
                                                //Event the message received.
                                                Received(message, m_LastTransmitted);

                                                //Mark disposed
                                                //Remove the message to avoid confusion
                                                using (m_LastTransmitted) m_LastTransmitted = null;

                                                //Reset the block
                                                m_InterleaveEvent.Reset();

                                                //Allow more waiting
                                                attempt = received = 0;

                                                goto Wait;
                                            }
                                            else if (++attempt <= m_MaximumTransactionAttempts /*|| m_RtspSocket.Available > 0*/)
                                            {
                                                //Might need to retransmit...

                                                goto Receive;
                                            }
                                        }
                                        else if (++attempt <= m_MaximumTransactionAttempts && 
                                                fatal is false &&
                                                SharesSocket is false &&
                                                IsConnected &&
                                                m_RtspSocket.IsNullOrDisposed() is false &&
                                                m_RtspSocket.Poll(m_SocketPollMicroseconds >> 4, SelectMode.SelectRead))
                                        {
                                            //Might need to retransmit...
                                            //Allow time for the remote host to respond.

                                            if (!System.Threading.Thread.Yield())
                                                System.Threading.Thread.Sleep(m_ConnectionTime);

                                            goto Receive;
                                        }
                                    }

                                    //else the sequenceNumberReceived is >= startSequenceNumber

                                    //Calculate the amount of time taken to receive the message.
                                    //Which is given by the time on the wall clock minus when the message was transferred or created.
                                    TimeSpan lastMessageRoundTripTime = DateTime.UtcNow - (message.Transferred ?? message.Created);

                                    //Ensure positive values for the RTT
                                    //if (lastMessageRoundTripTime < TimeSpan.Zero) lastMessageRoundTripTime = lastMessageRoundTripTime.Negate();

                                    //Assign it
                                    m_LastMessageRoundTripTime = lastMessageRoundTripTime.Duration();
                                }
                                //else
                                //{
                                //    //Calculate from elsewhere, e.g. m_LastTransmitted.
                                //}


                                //TODO
                                //REDIRECT (Handle loops)
                                //if(m_LastTransmitted.StatusCode == RtspStatusCode.MovedPermanently)

                                switch (m_LastTransmitted.RtspStatusCode)
                                {
                                    case RtspStatusCode.OK:
                                        if (message is not null)
                                        {
                                            //Ensure message is added to supported methods.
                                            SupportedMethods.Add(message.MethodString);
                                        }

                                        break;
                                    case RtspStatusCode.NotImplemented:
                                        if (m_LastTransmitted.CSeq.Equals(message.CSeq))
                                        {
                                            SupportedMethods.Remove(message.MethodString);
                                        }

                                        break;
                                    //case RtspStatusCode.MethodNotValidInThisState:
                                    //    {
                                    //        //Idea was to see if anything followed this message, e.g. back to back
                                    //        //if (m_LastTransmitted.ContainsHeader(RtspHeaders.Allow)) MonitorProtocol();

                                    //        break;
                                    //    }
                                    case RtspStatusCode.Unauthorized:
                                        //If we were not authorized and we did not give a nonce and there was an WWWAuthenticate header given then we will attempt to authenticate using the information in the header
                                        //If there was a WWWAuthenticate header in the response
                                        if (m_LastTransmitted.ContainsHeader(RtspHeaders.WWWAuthenticate) &&
                                            Credential is not null) //And there have been Credentials assigned
                                        {
                                            //Event the received message.
                                            Received(message, m_LastTransmitted);

                                            //Return the result of Authenticating with the given request and response (forcing the request if the credentails have not already been tried)
                                            return Authenticate(message, m_LastTransmitted);
                                        }

                                        //break
                                        break;
                                    case RtspStatusCode.RtspVersionNotSupported:
                                        {
                                            //if enforcing the version
                                            if (useClientProtocolVersion)
                                            {
                                                //Read the version from the response
                                                ProtocolVersion = m_LastTransmitted.Version;

                                                //Send the request again. SHOULD USE out error, 
                                                return SendRtspMessage(message, useClientProtocolVersion);
                                            }

                                            //break
                                            break;
                                        }
                                    default: break;
                                }

                                #region EchoXHeaders

                                //If the client should echo X headers
                                if (EchoXHeaders)
                                {
                                    //iterate for any X headers 
                                    foreach (string xHeader in m_LastTransmitted.GetHeaders().Where(h => h.Length > 2 && h[1] == Common.ASCII.HyphenSign && char.ToLower(h[0]) == 'x'))
                                    {
                                        //If contained already then update
                                        if (AdditionalHeaders.ContainsKey(xHeader))
                                        {
                                            AdditionalHeaders[xHeader] += ((char)Common.ASCII.SemiColon).ToString() + m_LastTransmitted.GetHeader(xHeader).Trim();
                                        }
                                        else
                                        {
                                            //Add
                                            AdditionalHeaders.Add(xHeader, m_LastTransmitted.GetHeader(xHeader).Trim());
                                        }
                                    }
                                }

                                #endregion

                                #region Parse Session Header

                                //For any other request besides teardown update the sessionId and timeout
                                if (message is not null &&
                                    message.RtspMethod is not RtspMethod.TEARDOWN)
                                {
                                    //Get the header.
                                    string sessionHeader = m_LastTransmitted[RtspHeaders.Session];

                                    //If there is a session header it may contain the option timeout
                                    if (string.IsNullOrWhiteSpace(sessionHeader) is false)
                                    {
                                        //Check for session and timeout

                                        //Get the values
                                        string[] sessionHeaderParts = sessionHeader.Split(RtspHeaders.SemiColon); //Only 2 sub strings...

                                        //RtspHeaders.ParseHeader(sessionHeader);

                                        int headerPartsLength = sessionHeaderParts.Length;

                                        //Check if a valid value was given
                                        if (headerPartsLength > 0)
                                        {
                                            //Trim it of whitespace
                                            string value = sessionHeaderParts.LastOrDefault(p => string.IsNullOrWhiteSpace(p) is false);

                                            //If we dont have an exiting id then this is valid if the header was completely received only.
                                            if (string.IsNullOrWhiteSpace(value) is false &&
                                                string.IsNullOrWhiteSpace(m_SessionId) ||
                                                false.Equals(string.Compare(value, m_SessionId) is Common.Binary.Zero))
                                            {
                                                //Get the SessionId if present
                                                m_SessionId = sessionHeaderParts[0].Trim();

                                                //Check for a timeout
                                                if (sessionHeaderParts.Length > 1)
                                                {
                                                    int timeoutStart = 1 + sessionHeaderParts[1].IndexOf(Media.Sdp.SessionDescription.EqualsSign);
                                                    if (timeoutStart >= Common.Binary.Zero && int.TryParse(sessionHeaderParts[1].AsSpan(timeoutStart), out timeoutStart))
                                                    {
                                                        //Should already be set...
                                                        m_RtspSessionTimeout = timeoutStart <= Common.Binary.Zero ? DefaultSessionTimeout : TimeSpan.FromSeconds(timeoutStart);
                                                    }
                                                }
                                            }

                                            //done
                                        }
                                        else if (string.IsNullOrWhiteSpace(m_SessionId))
                                        {
                                            //The timeout was not present
                                            m_SessionId = sessionHeader.Trim();

                                            m_RtspSessionTimeout = DefaultSessionTimeout;//Default
                                        }
                                    }
                                }

                                #endregion

                                #region CalculateServerDelay

                                if (CalculateServerDelay)
                                {

                                    RtspHeaders.TryParseTimestamp(m_LastTransmitted[RtspHeaders.Timestamp], out string timestamp, out m_LastServerDelay);

                                    timestamp = null;
                                }

                                #endregion

                                #region UpdateSession

                                if (string.IsNullOrWhiteSpace(m_SessionId) is false)
                                {
                                    //Update the session related

                                    if (m_Sessions.TryGetValue(m_SessionId, out RtspSession related))
                                    {
                                        //Todo, could return bool to indicate out of order or otherwise.
                                        related.UpdateMessages(message, m_LastTransmitted);

                                        related = null;
                                    }
                                }

                                #endregion

                                //Raise an event for the message received
                                Received(message, m_LastTransmitted);

                                //Todo, Event => CloseRequested...
                                ////string connection = m_LastTransmitted.GetHeader(RtspHeaders.Connection);

                                ////if (string.IsNullOrWhiteSpace(connection) is false && connection.IndexOf("close", StringComparison.InvariantCultureIgnoreCase) >= 0)
                                ////{
                                ////    Disconnect(true);

                                ////    if (AutomaticallyReconnect)
                                ////    {
                                ////        Connect();
                                ////    }
                                ////}

                            }//This client is in use...


                            break;

                        case RtspMessageType.Invalid: break;
                    }
                }

                #endregion
            }
            catch (Exception ex)
            {
                Common.ILoggingExtensions.Log(Logger, ToString() + "@SendRtspMessage: " + ex.Message);
                Common.ILoggingExtensions.LogException(Logger, ex);
            }
            finally
            {
                //Unblock (should not be needed)
                if (wasBlocked is false) m_InterleaveEvent.Set();
            }

            //Return the result
            //return message is not null && m_LastTransmitted is not null && message.CSeq == m_LastTransmitted.CSeq ? m_LastTransmitted : null;
            return m_LastTransmitted;

        }//Unchecked
    }

    /// <summary>
    /// Sends the Rtsp OPTIONS request
    /// </summary>
    /// <param name="useStar">The OPTIONS * request will be sent rather then one with the <see cref="RtspClient.CurrentLocation"/></param>
    /// <returns>The <see cref="RtspMessage"/> as a response to the request</returns>
    public RtspMessage SendOptions(Uri location, string sessionId = null, string connection = null)
    {
        using (var options = new RtspMessage(RtspMessageType.Request)
        {
            RtspMethod = RtspMethod.OPTIONS,
            Location = location ?? m_CurrentLocation,
            IsPersistent = true,
        })
        {
            if (string.IsNullOrWhiteSpace(sessionId) is false) options.SetHeader(RtspHeaders.Session, sessionId);

            bool isClosing = string.IsNullOrWhiteSpace(connection) is false;

            if (isClosing) options.SetHeader(RtspHeaders.Connection, connection);

            RtspMessage response = SendRtspMessage(options) ?? m_LastTransmitted;

            if (isClosing is false && response is not null)
            {
                //Get the Public header which indicates the methods supported by the client
                string publicMethods = response[RtspHeaders.Public];

                //If there is Not such a header then return the response
                if (string.IsNullOrWhiteSpace(publicMethods) is false)
                {
                    //Process values in the Public header.
                    foreach (string method in publicMethods.Split(RtspHeaders.Comma))
                    {
                        SupportedMethods.Add(method.Trim());
                    }
                }

                //Should have a way to keep the allowed seperate.
                string allowedMethods = response[RtspHeaders.Allow];

                //If there is Not such a header then return the response
                if (string.IsNullOrWhiteSpace(allowedMethods) is false)
                {
                    //Process values in the Public header.
                    foreach (string method in allowedMethods.Split(RtspHeaders.Comma))
                    {
                        SupportedMethods.Add(method.Trim());
                    }
                }

                //Some servers only indicate different features at the SETUP level...

                string supportedFeatures = response[RtspHeaders.Supported];

                //If there is Not such a header then return the response
                if (string.IsNullOrWhiteSpace(supportedFeatures) is false)
                {
                    //Process values in the Public header.
                    foreach (string method in supportedFeatures.Split(RtspHeaders.Comma))
                    {
                        SupportedFeatures.Add(method.Trim());
                    }
                }
            }

            response = m_LastTransmitted;

            if (response is null && isClosing is false && IsPlaying is false)
            {
                if (options.Location.Equals(RtspMessage.Wildcard))
                    Media.Common.TaggedExceptionExtensions.RaiseTaggedException(this, "Unable to get options, See InnerException.", new Common.TaggedException<RtspMessage>(response, "See Tag for Response."));
                else
                    return SendOptions(RtspMessage.Wildcard, sessionId, connection);
            }

            return response;
        }
    }

    public RtspMessage SendOptions(bool useStar = false, string sessionId = null, string connection = null)
    {
        return SendOptions(useStar ? RtspMessage.Wildcard : CurrentLocation, sessionId, connection);
    }

    /// <summary>
    /// Assigns the SessionDescription returned from the server
    /// </summary>
    /// <returns></returns>
    public RtspMessage SendDescribe() //bool force, bool followRedirects
    {

        RtspMessage response = null;

        try
        {
            using (RtspMessage describe = new(RtspMessageType.Request)
            {
                RtspMethod = RtspMethod.DESCRIBE,
                Location = CurrentLocation,
                IsPersistent = true
            })
            {
                #region Reference

                // The DESCRIBE method retrieves the description of a presentation or
                // media object identified by the request URL from a server. It may use
                // the Accept header to specify the description formats that the client
                // understands. The server responds with a description of the requested
                // resource. The DESCRIBE reply-response pair constitutes the media
                // initialization phase of RTSP.

                #endregion

                describe.SetHeader(RtspHeaders.Accept, Sdp.SessionDescription.MimeType);

                Describe:
                response = SendRtspMessage(describe, out SocketError error, true, true, m_MaximumTransactionAttempts) ?? m_LastTransmitted;

                if (response is null) return response;

                //Handle no response
                //If the remote end point is just sending Interleaved Binary Data out of no where it is possible to continue without a SessionDescription

                if (Common.IDisposedExtensions.IsNullOrDisposed(response ??= m_LastTransmitted)) Media.Common.TaggedExceptionExtensions.RaiseTaggedException(describe, "Unable to describe media, no response to DESCRIBE request. The request is in the Tag property.");
                else response.IsPersistent = true;

                if (response.IsComplete is false)
                {
                    //Wait for complete responses
                    if (SharesSocket)
                    {
                        m_InterleaveEvent.Wait();
                    }
                    else
                    {
                        response.CompleteFrom(m_RtspSocket, m_Buffer);
                    }
                }

                RtspStatusCode statusCode = response.RtspStatusCode;

                switch (statusCode)
                {
                    //don't handle erroneous responses
                    case RtspStatusCode.Unauthorized:
                        {

                            //HandleAuthentication(RtspMessage r, resp, ) =>

                            if (m_LastTransmitted.ContainsHeader(RtspHeaders.WWWAuthenticate) &&
                                Credential is not null) //And there have been Credentials assigned
                            {
                                //Event the received message.
                                Received(response, m_LastTransmitted);

                                //Return the result of Authenticating with the given request and response (forcing the request if the credentails have not already been tried)
                                return Authenticate(response, m_LastTransmitted);
                            }
                            break;
                        }
                    case RtspStatusCode.InternalServerError:
                        {
                            //Only handle responses for the describe request sent when sharing the socket
                            if (response.CSeq.Equals(describe.CSeq) is false)
                            {
                                describe.RemoveHeader(RtspHeaders.Timestamp);

                                describe.RemoveHeader(RtspHeaders.CSeq);

                                goto Describe;
                            }

                            return response;
                        }
                    //Handle NotFound
                    case RtspStatusCode.NotFound:
                        {
                            Media.Common.TaggedExceptionExtensions.RaiseTaggedException(describe, "Unable to describe media, NotFound. The response is in the Tag property.");
                            break;
                        }
                    default:
                        {
                            if (response.RtspMethod is RtspMethod.REDIRECT) goto case RtspStatusCode.Found;
                            if (statusCode > RtspStatusCode.OK) goto case RtspStatusCode.InternalServerError;
                            else if (statusCode < RtspStatusCode.OK) goto case RtspStatusCode.OK;
                            break;
                        }
                    case RtspStatusCode.OK:
                        {
                            string contentType = response[RtspHeaders.ContentType];

                            //Handle any not ok response (allow Continue)
                            //Handle MultipleChoice for Moved or ContentType...
                            if (response.RtspStatusCode >= RtspStatusCode.MultipleChoices &&
                                false.Equals(string.IsNullOrEmpty(contentType)) &&
                                string.Compare(contentType.TrimStart(), Sdp.SessionDescription.MimeType, true) is 0 is false)
                            {
                                Media.Common.TaggedExceptionExtensions.RaiseTaggedException(response.RtspStatusCode, "Unable to describe media. The StatusCode is in the Tag property.");
                            }
                            //else if (response.IsComplete && string.IsNullOrWhiteSpace(response.Body))
                            //{
                            //    Media.Common.TaggedExceptionExtensions.RaiseTaggedException(this, "Unable to describe media, Missing Session Description");
                            //}

                            #region MS-RTSP

                            //////Not really needed

                            ////string playListId = response["X-Playlist-Gen-Id"];

                            ////if (false == string.IsNullOrWhiteSpace(playListId))
                            ////{
                            ////    AdditionalHeaders.Add("X-Playlist-Gen-Id", playListId.Trim());
                            ////}

                            //// Should also do a SET_PARAMETER
                            //Content-type: application/x-rtsp-udp-packetpair;charset=UTF-8\r\n\r\n
                            //Content-Length: X \r\n
                            //type: high-entropy-packetpair variable-size

                            #endregion

                            //Try to create a session description even if there was no contentType so long as one was not specified against sdp.
                            m_SessionDescription = new Sdp.SessionDescription(response.Body);

                            //No longer being used.
                            describe.IsPersistent = false;

                            //break;
                            //Handle the rest like Found...
                            goto case RtspStatusCode.Found;
                        }
                    case RtspStatusCode.Found:
                        {
                            //Todo, check redirect length.
                            //UriDecode?

                            //Determine if there is a new location
                            string newLocation = response.GetHeader(RtspHeaders.Location);

                            if (string.IsNullOrWhiteSpace(newLocation) is false)
                            {
                                newLocation = newLocation.Trim();
                            }

                            //We start at our location
                            Uri baseUri = m_CurrentLocation;

                            //Get the content-base or content-location header
                            string contentBase;

                            //If any header was present
                            if ((string.IsNullOrWhiteSpace(contentBase = response[RtspHeaders.ContentBase]) is false ||
                                 string.IsNullOrWhiteSpace(contentBase = response[RtspHeaders.ContentLocation]) is false ||
                                 string.IsNullOrWhiteSpace(contentBase = newLocation) is false))
                            {

                                //@ System.UriPartial...

                                //Try to create a Uri from the string
                                if (Uri.TryCreate(contentBase, UriKind.RelativeOrAbsolute, out baseUri))
                                {
                                    //If the Uri was not absolute
                                    if (baseUri.IsAbsoluteUri is false)
                                    {
                                        //Try to make it absolute to the CurrentLocation and if it cannot then raise an exception
                                        if (Uri.TryCreate(m_CurrentLocation, baseUri, out baseUri) is false)
                                        {
                                            Media.Common.TaggedExceptionExtensions.RaiseTaggedException(contentBase, "See Tag. Can't parse ContentBase header.");
                                        }

                                    }
                                    else //The Uri was absolute
                                    {
                                        if (IPAddress.TryParse(m_CurrentLocation.Host, out IPAddress remoteHost) && m_RemoteHostAddresses.Contains(remoteHost))
                                        {
                                            goto IsSameHost;
                                        }

                                        IPHostEntry remoteHostEntry = Dns.GetHostEntry(m_CurrentLocation.Host);

                                        foreach(IPAddress address in remoteHostEntry.AddressList)
                                        {
                                            if (m_RemoteHostAddresses.Contains(address))
                                            {
                                                goto IsSameHost;
                                            }
                                        }

                                        Media.Common.TaggedExceptionExtensions.RaiseTaggedException(this, "The server issued a response which indicates a required resource from another host.", new Common.TaggedException<RtspMessage>(m_LastTransmitted, "New Host Connection Required. See tag."));

                                    IsSameHost:

                                        //Check for the Scheme to change
                                        if (baseUri.Scheme.Equals(m_CurrentLocation.Scheme, StringComparison.OrdinalIgnoreCase) is false)
                                        {
                                            //doesn't handle changing scheme...
                                            //m_CurrentLocation.Scheme.StartsWith(baseUri.Scheme, StringComparison.OrdinalIgnoreCase) is false)

                                            //E.g.

                                            //Will handle when given general and get specific or vice versa
                                            //rtsp and given rtspt
                                            //rtsp and given rtspu
                                            //rtspu and given rtsp
                                            //rtspt and given rtsp

                                            //Will not handle specific given something else
                                            //rtspt and given rtspu
                                            //rtspu and given rtspt

                                            if (m_CurrentLocation.Scheme.StartsWith(baseUri.Scheme, StringComparison.OrdinalIgnoreCase) is false)
                                            {
                                                Media.Common.TaggedExceptionExtensions.RaiseTaggedException(this, "The server issued a response which indicates a required resource from another scheme.", new Common.TaggedException<RtspMessage>(m_LastTransmitted, "New Host Connection Required. See tag."));
                                            }
                                        }//Check for the port to change

                                        if (baseUri.Port.Equals(m_CurrentLocation.Port) is false &&
                                                baseUri.Port.Equals(m_RtspPort) is false)
                                        {
                                            //The host or port is not the same..

                                            //if we can reconnect
                                            if (AutomaticallyReconnect)
                                            {
                                                //Reconnect
                                                CurrentLocation = baseUri;
                                            }
                                            else
                                            {
                                                //Throw an exception
                                                Media.Common.TaggedExceptionExtensions.RaiseTaggedException(this, "The server issued a response which indicates a required resource from another port.", new Common.TaggedException<RtspMessage>(m_LastTransmitted, "New Host Connection Required. See tag."));
                                            }

                                            //Return the response as given from the new connection.
                                            return response = SendDescribe() ?? response;
                                        }
                                        else if (m_CurrentLocation.Equals(baseUri) is false)  //Just the path has changed....                                        
                                        {

                                            //string originalString = baseUri.OriginalString;

                                            ////Check for the path seperator...

                                            //char lastChar = originalString.Last();

                                            //if (lastChar.Equals((char)Common.ASCII.ForwardSlash) ||
                                            //    lastChar.Equals((char)Common.ASCII.BackSlash))
                                            //{
                                            //    //Could only take the different part of the location with the following code
                                            //    //parsedLocation.MakeRelativeUri(Location)

                                            //    //Redirect to the Location by setting Location. (Allows a new host)

                                            //    //Create a Uri without the path seperator.
                                            //    baseUri = new Uri(baseUri.OriginalString.Substring(0, baseUri.OriginalString.Length - 1));
                                            //}

                                            //Update the CurrentLocation (Event)
                                            m_CurrentLocation = baseUri;

                                            //Maybe better because the docs indicate relative...
                                            //newLocation = baseUri.PathAndQuery;
                                        }

                                        ///

                                    }
                                }
                                else
                                {
                                    //Could not parse the header...
                                    Media.Common.TaggedExceptionExtensions.RaiseTaggedException(this, "Cannot parse required header. See tag of InnerException.", new Common.TaggedException<RtspMessage>(m_LastTransmitted, "Cannot parse required header. See tag."));
                                }
                            }
                        }

                        //The response was handled.
                        break;
                }
            }
        }
        catch (Common.TaggedException<RtspClient>)
        {
            throw;
        }
        catch (Common.TaggedException<SessionDescription> sde)
        {
            Media.Common.TaggedExceptionExtensions.RaiseTaggedException(this, "Unable to describe media, Session Description Exception Occured.", sde);
        }
        catch (Exception ex)
        {
            if (ex is Media.Common.ITaggedException) throw;

            Media.Common.TaggedExceptionExtensions.RaiseTaggedException(this, "An error occured", ex);
        }

        //Return the response
        return response;
    }

    /// <summary>
    /// Sends a request which will remove the session given from the server using a TEARDOWN * request.
    /// </summary>
    /// <param name="sessionId">The sessionId to remove, if null the current <see cref="SessionId"/> will be used if possible.</param>
    /// <param name="closeConnection">Indicates if the `Connection` header of the request should be set to 'Close'</param>
    /// <returns></returns>
    public virtual RtspMessage RemoveSession(string sessionId, bool closeConnection = false)
    {
        using (var teardown = new RtspMessage(RtspMessageType.Request))
        {
            teardown.RtspMethod = RtspMethod.TEARDOWN;

            if (closeConnection) teardown.SetHeader(RtspHeaders.Connection, "Close");

            sessionId ??= m_SessionId;

            if (string.IsNullOrWhiteSpace(sessionId) is false) teardown.SetHeader(RtspHeaders.Session, sessionId);

            //Calling on stopping here indicates all sessions end...
            //SHould get the session by id and then use it's media description in the event.
            OnStopping();

            try { return SendRtspMessage(teardown); }
            finally { m_SessionId = null; }
        }
    }

    public RtspMessage SendTeardown(bool useStar = false, string sessionId = null, string connection = null)
    {
        return SendTeardown(useStar ? RtspMessage.Wildcard : m_CurrentLocation, sessionId, connection);
    }

    public RtspMessage SendTeardown(Uri location, string sessionId = null, string connection = null, bool hasResponse = false)
    {
        //Return the result of the Teardown
        using (var teardown = new RtspMessage(RtspMessageType.Request)
        {
            RtspMethod = RtspMethod.TEARDOWN,
            Location = location
        })
        {
            //Set the close header if disconnecting
            if (string.IsNullOrWhiteSpace(connection) is false)
            {
                teardown.SetHeader(RtspHeaders.Connection, connection);
            }

            //Send the request and if not closing the connecting then wait for a response
            return SendRtspMessage(teardown, true, hasResponse);
        }
    }

    public RtspMessage SendTeardown(MediaDescription mediaDescription = null, bool disconnect = false, bool force = false, string connection = null)
    {
        RtspMessage response = null;

        //Check if the session supports pausing a specific media item
        if (Common.IDisposedExtensions.IsNullOrDisposed(mediaDescription) is false &&
            false.Equals(SessionDescription.SupportsAggregateMediaControl(CurrentLocation))) throw new InvalidOperationException("The SessionDescription does not allow aggregate control.");

        //only send a teardown if not forced and the client is playing
        if (force is false && IsPlaying is false) return response;

        try
        {
            //If there is a client then stop the flow of this media now with RTP
            if (Common.IDisposedExtensions.IsNullOrDisposed(m_RtpClient) is false)
            {
                //Send a goodbye for all contexts if the mediaDescription was not given
                if (Common.IDisposedExtensions.IsNullOrDisposed(mediaDescription))
                {
                    if (SharesSocket is false) m_RtpClient.Deactivate();
                    else m_RtpClient.SendGoodbyes();
                }
                else//Find the context for the description
                {
                    //Get a context
                    RtpClient.TransportContext context = m_RtpClient.GetContextForMediaDescription(mediaDescription);

                    //If context was determined then send a goodbye
                    if (Common.IDisposedExtensions.IsNullOrDisposed(context) is false)
                    {
                        //Send a goodbye now (but still allow reception)
                        m_RtpClient.SendGoodbye(context);

                        //Dispose context?

                        //Remove the reference
                        context = null;
                    }
                }
            }

            //Keep track of whats playing
            if (Common.IDisposedExtensions.IsNullOrDisposed(mediaDescription))
            {
                m_Playing.Clear();

                //LeaveOpen = false;
            }
            else m_Playing.Remove(mediaDescription);

            //The media is stopping now.
            OnStopping(mediaDescription);

            //Return the result of the Teardown
            return SendTeardown(Common.IDisposedExtensions.IsNullOrDisposed(mediaDescription) ? m_CurrentLocation : mediaDescription.GetAbsoluteControlUri(CurrentLocation, SessionDescription),
                SessionId,
                connection,
                disconnect);
        }
        catch (Common.TaggedException<RtspClient>)
        {
            return response;
        }
        finally
        {
            //Ensure the sessionId is invalided when no longer playing if not forced
            if (force is false && IsPlaying is false) m_SessionId = null;

            //If there is nothing playing now we can deactivate the RtpClient.
            if (m_Playing.Count is 0 && m_RtpClient.IsActive) m_RtpClient.Deactivate();
        }
    }

    public RtspMessage SendSetup(MediaDescription mediaDescription)
    {
        if (Common.IDisposedExtensions.IsNullOrDisposed(mediaDescription)) throw new ArgumentNullException("mediaDescription");

        //Send the setup
        return SendSetup(mediaDescription.GetAbsoluteControlUri(m_CurrentLocation, SessionDescription), mediaDescription);
    }

    /// <summary>
    /// Sends a SETUP Rtsp Request based on the given parameters. If no response is obtained a second response will be sent.
    /// If no response is received the client WILL attempt to proceed as if SETUP has succeeded and setup any transport resources which would be required.
    /// If the response indicates the session is not found and there is not already an existing <see cref="SessionId"/> the request will be repeated without a Session header.
    /// </summary>
    /// <param name="location"></param>
    /// <param name="mediaDescription"></param>
    /// <param name="unicast"></param>
    /// <returns></returns>
    //Remove unicast... and allow for session based setup
    internal RtspMessage SendSetup(Uri location, MediaDescription mediaDescription, bool unicast = true, string mode = null)//False to use manually set protocol
    {
        if (location is null) throw new ArgumentNullException(nameof(location));

        if (Common.IDisposedExtensions.IsNullOrDisposed(mediaDescription)) throw new ArgumentNullException(nameof(mediaDescription));

        //Todo Setup should only create a TransportContext which COULD then be given to a RtpClient 
        //This will allow for non RTP transports to be used such as MPEG-TS.

        //Could also use out bool allocatedContext...

        //Must de-coulple the RtpClient and replace it

        //Todo, Add CreateSession logic here and give the session to SendSetup.
        //Move this logic to the overload with the RtspSession,

        //e.g. something like this SendSetup(GetOrCreateSession(mediaDescription))
        //But should ensure that sessions are not created when they are not supposed to be e.g. when the response is not a success. 

        //Determine if a client_port datum should be sent in the Transport header as a set, list or a single entry
        bool needsRtcp = true, multiplexing = false;

        //Todo, a new connection may be needed for each session when the Bandwidth requirements are high...

        #region [WMS Notes / Log]

        //Some sources indicate that rtx must be or must not be setup
        //They also say that only one ssrc should be sent
        //There are various different advices.

        //Check the spec if you have doubts https://msdn.microsoft.com/en-us/library/cc245238.aspx

        /* WMS Server
         1. 
        client -->
        SETUP rtsp://s-media1/spider_od/rtx RTSP/1.0
        Transport:
        RTP/AVP/UDP;unicast;client_port=1206-1207;ssrc=9cef6565;mode=PLAY

        server <--
        RTSP/1.0 200 OK
        Transport:
        RTP/AVP/UDP;unicast;server_port=5004-5005;client_port=1206-1207;ssrc=e34
        90f0d;mode=PLAY

        2. 
        client -->
        SETUP rtsp://s-media1/spider_od/audio RTSP/1.0
        Transport: RTP/AVP/UDP;unicast;client_port=1208;ssrc=9a789797;mode=PLAY 

        Server <--
        RTSP/1.0 200 OK
        Transport:
        RTP/AVP/UDP;unicast;server_port=5004;client_port=1208;ssrc=8873c0ac;mode
        =PLAY

        3. 
        client -->
        SETUP rtsp://s-media1/spider_od/video RTSP/1.0
        Transport: RTP/AVP/UDP;unicast;client_port=1208;ssrc=275f7979;mode=PLAY

        Server <--
        RTSP/1.0 200 OK
        Transport:
        RTP/AVP/UDP;unicast;server_port=5004;client_port=1208;ssrc=8873c0cf;mode
        =PLAY
         */

        ////Keep the values parsed from the description
        //int rr, rs, a;

        ////Attempt to parse them
        //if (RtpClient.TransportContext.TryParseBandwidthDirectives(mediaDescription, out rr, out rs, out a) &&
        //    rr is 0 && //If the rr AND
        //    rs is 0/* && a is 0*/) // rs directive specified 0 (Should check AS?)
        //{
        //    //RTSP is not needed
        //    needsRtcp = false;
        //}

        ////Rtx streams for a WMS Server always require RTCP?
        ////Should ensure this convention doesn't interfere with names where are not for WMS
        ////Possible check server header in m_LastTransmitted
        //if (location.AbsoluteUri.EndsWith("rtx", StringComparison.OrdinalIgnoreCase)) needsRtcp = true;

        #endregion

        try
        {
            //Should either create context NOW or use these sockets in the created context.

            //may need a new rtsp socket when using interleaved and the bandwidth is low or the latency is high

            //Create sockets to reserve the ports we think we will need.
            Socket rtpTemp = null, rtcpTemp = null;

            using (RtspMessage setup = new(RtspMessageType.Request)
            {
                RtspMethod = RtspMethod.SETUP,
                Location = location
            })
            {

                //Values in the header we need
                int clientRtpPort = -1, clientRtcpPort = -1,
                    serverRtpPort = -1, serverRtcpPort = -1,
                    //Darwin and Wowza uses this ssrc, VLC/Live Gives a Unsupported Transport, WMS and most others seem to ignore it.
                    localSsrc = 0,//RFC3550.Random32(),  
                    remoteSsrc = 0;

                //Should check this.
                //m_RtspSocket.LocalEndPoint.AddressFamily != AddressFamily.InterNetwork && m_RtspSocket.LocalEndPoint.AddressFamily != AddressFamily.InterNetworkV6

                //Cache this to prevent having to go to get it every time down the line
                IPAddress localIp = ((IPEndPoint)m_RtspSocket.LocalEndPoint).Address,
                    sourceIp = localIp.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, //Todo, may not be Internetwork...
                    destinationIp = sourceIp;

                //Todo, check the MediaDescription ConnectionLine for the ConnectionAddress to have a Multicast address
                bool multicast = false, interleaved = m_RtpProtocol == ProtocolType.Tcp;

                byte dataChannel = 0, controlChannel = 1;

                //Todo, get this from the connection address.
                int ttl = 255;

                //8? should be determine by rtcp enabled and the type of packets, rtcp 4, rtp 12
                int minimumPacketSize = 8, maximumPacketSize = m_Buffer.Count;

                //12.7 Blocksize
                /*
                 This request header field is sent from the client to the media server
                    asking the server for a particular media packet size. This packet
                    size does not include lower-layer headers such as IP, UDP, or RTP.
                    The server is free to use a blocksize which is lower than the one
                    requested. The server MAY truncate this packet size to the closest
                    multiple of the minimum, media-specific block size, or override it
                    with the media-specific size if necessary. The block size MUST be a
                    positive decimal number, measured in octets. The server only returns
                    an error (416) if the value is syntactically invalid.
                 */

                //This is important if the server can support it, it will ensure that packets can fit in the buffer.
                //It also tells the server what our buffer size is so if they wanted they could intentionally make packets which allowed only a certain amount of bytes remaining in the buffer....

                //This NEEDS TO BE BASED AROUND THE LIMITS OF THE NETWORK MTU AND MSS
                if (SendBlocksize) setup.SetHeader(RtspHeaders.Blocksize, m_Buffer.Count.ToString());

                //TODO
                // IF TCP was specified or the MediaDescription specified we need to use Tcp as specified in RFC4571
                // DETERMINE if only 1 channel should be sent in the TransportHeader if we know RTCP is not going to be used. (doing so would force RTCP to stay disabled the entire stream unless a SETUP for just the RTCP occured, how does one setup just a RTCP session..)

                //Send any supported headers?

                //if (false == setup.ContainsHeader(RtspHeaders.Supported))
                //{
                //setup.SetHeader(RtspHeaders.Supported, "play.basic, play.scale, play.speed, setup.playing, con.persistent, con.independent, con.transient, rtp.mux, rtcp.mux, ts.mux, raw.mux, mux.*");
                //setup.SetHeader(RtspHeaders.Supported, string.Join(Sdp.SessionDescription.SpaceString, SupportedFeatures));
                //}

                //Required: header (RequiredFeatures)
                if (RequiredFeatures.Count > 0 &&
                    setup.ContainsHeader(RtspHeaders.Require) is false)
                {
                    setup.SetHeader(RtspHeaders.Require, string.Join(Sdp.SessionDescription.SpaceString, RequiredFeatures));
                }

                //Todo, Have field / property for PortRanges
                //keep track to avoid exceptions if possible.
                int lastPortUsed = 10000;

                //Interleaved
                if (interleaved)
                {

                    //Check for option UseNewConnectionForEachTrack
                    //If enabled then create a new socket

                    //else{

                    //RTCP-mux:

                    //If there is already a RtpClient with at-least 1 TransportContext
                    if (Common.IDisposedExtensions.IsNullOrDisposed(m_RtpClient) is false)
                    {
                        RtpClient.TransportContext lastContext = m_RtpClient.GetTransportContexts().LastOrDefault();

                        if (Common.IDisposedExtensions.IsNullOrDisposed(lastContext) is false
                            && lastContext.IsActive)
                        {
                            setup.SetHeader(RtspHeaders.Transport,
                                RtspHeaders.TransportHeader(RtpClient.RtpAvpProfileIdentifier + "/TCP", //connection type
                                localSsrc != 0 ? localSsrc : null, //ssrc
                                null, null, null, null, null, //source and ports
                                true, false, //unicast, multicast
                                null, //ttl
                                true, //interleaved
                                dataChannel = (byte)(lastContext.DataChannel + 2), //dataChannel
                                (needsRtcp ? (controlChannel = (byte)(lastContext.ControlChannel + 2)) : null), //controlChannel
                                mode)); //mode
                        }
                        else
                        {
                            setup.SetHeader(RtspHeaders.Transport, //name, value =>
                                RtspHeaders.TransportHeader(RtpClient.RtpAvpProfileIdentifier + "/TCP", //connection type
                                localSsrc != 0 ? localSsrc : null, //ssrc
                                null, null, null, null, null, //source and ports
                                true, false, //unicast, multicast
                                null, //ttl
                                true, //interleaved
                                dataChannel, (needsRtcp ? controlChannel : null), //dataChannel, controlChannel
                                mode)); //mode
                        }
                    }
                }
                else if (string.Compare(mediaDescription.MediaProtocol, RtpClient.RtpAvpProfileIdentifier, true) is 0) // We need to find an open Udp Port
                {

                    //Revise
                    //Is probably Ip, set to Udp
                    m_RtpProtocol = ProtocolType.Udp;

                    //If multicast then the port doesn't have to be reserved, may need reuse port.
                    if (multicast is false)
                    {
                        //Could send 0 to have server pick port?                        

                        if (Common.IDisposedExtensions.IsNullOrDisposed(m_RtpClient) is false)
                        {
                            //Select which port to use, SelectMediaPort()
                            RtpClient.TransportContext lastContext = m_RtpClient.GetTransportContexts().LastOrDefault();

                            if (Common.IDisposedExtensions.IsNullOrDisposed(lastContext) is false &&
                                lastContext.IsActive)
                            {
                                lastPortUsed = ((IPEndPoint)(lastContext.LocalRtp ?? lastContext.LocalRtcp)).Port + 1;

                                lastContext = null;

                            }
                            else
                            {
                                lastPortUsed = 9999;
                            }
                        }

                        //Should allow this to be given or set as a property MinimumUdpPort, MaximumUdpPort                        
                        int openPort = Media.Common.Extensions.Socket.SocketExtensions.ProbeForOpenPort(ProtocolType.Udp, lastPortUsed + 1, true);

                        if (openPort is Common.Binary.NegativeOne)
                            Media.Common.TaggedExceptionExtensions.RaiseTaggedException(this, "Could not find open Udp Port");
                        //else if (MaximumUdp.HasValue && openPort > MaximumUdp)
                        //{
                        //    Media.Common.Extensions.Exceptions.ExceptionExtensions.CreateAndRaiseException(this, "Found Udp Port > MaximumUdp. Found: " + openPort);
                        //}    

                        rtpTemp = Media.Common.Extensions.Socket.SocketExtensions.ReservePort(SocketType.Dgram, ProtocolType.Udp, ((IPEndPoint)m_RtspSocket.LocalEndPoint).Address, clientRtpPort = openPort);

                        //Check for muxing of rtp and rtcp on the same physical port
                        if (mediaDescription.Where(l => l.Type == Sdp.Lines.SessionAttributeLine.AttributeType && l.Parts.Any(p => p.ToLowerInvariant() == "rtcp-mux")).Any())
                        {
                            //Might not 'need' it
                            needsRtcp = multiplexing = true;

                            //Use the same port
                            clientRtcpPort = clientRtpPort;
                        }
                        else if (needsRtcp)
                        {
                            //Should probably check for open port again...

                            rtcpTemp = Media.Common.Extensions.Socket.SocketExtensions.ReservePort(SocketType.Dgram, ProtocolType.Udp, ((IPEndPoint)m_RtspSocket.LocalEndPoint).Address, (clientRtcpPort = (openPort is ushort.MaxValue or 0 ? openPort : openPort + 1)));
                        }
                    }

                    //Supposedly
                    //WMS Server will complain if there is a RTCP port and no RTCP is allowed.
                    //More then likely only Ross will complain or his shitty software.

                    //Should allow a Rtcp only setup? would be a different profile...
                    setup.SetHeader(RtspHeaders.Transport, RtspHeaders.TransportHeader(RtpClient.RtpAvpProfileIdentifier + "/UDP", localSsrc != 0 ? localSsrc : null, null, clientRtpPort, (needsRtcp ? clientRtcpPort : null), null, null, false == multicast, multicast, null, false, 0, 0, RtspMethod.PLAY.ToString()));
                }
                else throw new NotSupportedException("The required Transport is not yet supported.");

                SocketError error;

                bool triedTwoTimes = false;


            //Make new connection if required.

            Setup:
                //Get the response for the setup
                RtspMessage response = SendRtspMessage(setup, out error, out int sequenceNumber, true, true, m_MaximumTransactionAttempts) ?? m_LastTransmitted;

                if (!response.IsSuccessful) return response;

                //Should check the cSeq or content type... 

                //Switch on the error
                switch (error)
                {
                    case SocketError.Success:
                        {
                            //Get a session ready                    

                            //Create a RtspSession if there is not already one assoicated with the current sessionId
                            if (m_Sessions.TryGetValue(SessionId, out RtspSession session) is false)
                            {
                                //Message possibly not complete... Session header may be truncated...

                                //Create a session
                                session = new RtspSession(this, setup, response)
                                {
                                    EnableKeepAliveRequest = DisableKeepAliveRequest is false,
                                    ControlLocation = location
                                };

                                //Only if there was a sessionId found
                                if (string.IsNullOrWhiteSpace(session.SessionId) is false)
                                {
                                    //Add the session created
                                    if (m_Sessions.TryAdd(SessionId, session) is false)
                                    {
                                        Common.ILoggingExtensions.Log(Logger, $"Could not add Session: {SessionId}");
                                    }
                                }
                            }

                            //Ensure there was a response
                            if (response is null) goto NoResponse;

                            //Todo, check if this is how events should be handled.
                            //if (null == response) return response;

                            //Response not OK
                            if (response.RtspStatusCode != RtspStatusCode.OK)
                            {
                                //Transport requested not valid
                                if (response.RtspStatusCode == RtspStatusCode.UnsupportedTransport && m_RtpProtocol != ProtocolType.Tcp)
                                {
                                    goto SetupTcp;
                                }
                                else if (response.RtspStatusCode == RtspStatusCode.SessionNotFound && //If the session was not found
                                    string.IsNullOrWhiteSpace(m_SessionId) is false && //And there IS an existing session id
                                    triedTwoTimes is false) //And setup has not already been attempted two times.
                                {

                                    //Dispose old sessions?

                                    //Erase the old session id
                                    m_SessionId = string.Empty;

                                    //Attempt the setup again
                                    return SendSetup(location, mediaDescription);
                                }
                                else //Not Ok and not Session Not Found
                                {
                                    //If there was an initial location and that location's host is different that the current location's host
                                    if (m_InitialLocation is not null && location.Host.Equals(m_InitialLocation.Host) is false)
                                    {
                                        //You would have thought that the resource we were directed to would be able to handle it's own DNS routing even when it's not tunneled through IPv4

                                        //Try to use the old location
                                        location = mediaDescription.GetAbsoluteControlUri(m_InitialLocation, SessionDescription);

                                        //Don't try again
                                        triedTwoTimes = true;

                                        goto Setup;
                                    }

                                    //Media.Common.TaggedExceptionExtensions.RaiseTaggedException(response.StatusCode, "Unable to setup media. The status code is in the Tag property.");

                                    //Return the response given
                                    return response;
                                }
                            }

                            //Handle the servers response for Blocksize                    

                            string blockSize = response[RtspHeaders.Blocksize];

                            if (string.IsNullOrWhiteSpace(blockSize) is false)
                            {
                                //Extract the value (Should account for ';' in some way)
                                blockSize = Media.Common.ASCII.ExtractNumber(blockSize.Trim());

                                try
                                {
                                    //Parse it...
                                    maximumPacketSize = int.Parse(blockSize, System.Globalization.NumberStyles.Integer);

                                    //If the packets cannot fit in the buffer
                                    if (maximumPacketSize > m_Buffer.Count && m_RtspSocket.ProtocolType == ProtocolType.Tcp)
                                    {
                                        //Try to allow processing to resize buffer.
                                        Media.Common.TaggedExceptionExtensions.RaiseTaggedException(maximumPacketSize, "Media Requires a Larger Buffer. (See Tag for value)");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Media.Common.TaggedExceptionExtensions.RaiseTaggedException(response, "BlockSize of the response needs consideration. (See Tag for response)", ex);
                                }
                            }

                            //Handle Rtcp-Interval (eventually, or definitely when it becomes a standard header :P)

                            //Handle anything else

                            //When jumping here we return null even though we allocate a context.
                            NoResponse:

                            //We SHOULD have a valid TransportHeader in the response
                            //Get the transport header from the response if present.
                            session.TransportHeader = response?[RtspHeaders.Transport];

                            //If there was no return transport header then we don't know what ports to utilize for reception.
                            if (string.IsNullOrWhiteSpace(session.TransportHeader))
                            {
                                //Discover them when receiving from the host
                                serverRtpPort = 0;

                                serverRtcpPort = 0;
                            }
                            else
                            {
                                //Check for the RTP token to ensure the underlying tranport is supported.
                                //Eventually any type such as RAW etc will be supported.
                                if (session.TransportHeader.Contains("RTP") is false
                                ||
                                RtspHeaders.TryParseTransportHeader(session.TransportHeader,
                                out remoteSsrc, out sourceIp, out serverRtpPort, out serverRtcpPort, out clientRtpPort, out clientRtcpPort,
                                out interleaved, out dataChannel, out controlChannel, out mode, out unicast, out multicast, out destinationIp, out ttl) is false)
                                    Media.Common.TaggedExceptionExtensions.RaiseTaggedException(this, "Cannot setup media, Invalid Transport Header in Rtsp Response: " + session.TransportHeader);
                            }

                            //If the server returns a channel which is already in use
                            //it then determines if there is an existing channel already utilized by this client with a different socket.
                            //If there is, then nothing neeed to be created just updated.
                            //Todo
                            //Care should be taken that the SDP is not directing us to connect to some unknown resource....

                            //Just incase the source datum was not given, only for unicast connections.
                            if ((multicast is false && sourceIp.Equals(IPAddress.Any) || sourceIp.Equals(IPAddress.IPv6Any)) && m_RtspSocket.RemoteEndPoint is IPEndPoint rep) sourceIp = rep.Address;

                            //If multicast was given check the destination address and if was not specified use the sourceIp.
                            if (multicast && (destinationIp.Equals(IPAddress.Any) || destinationIp.Equals(IPAddress.IPv6Any))) destinationIp = sourceIp;

                            //Create the context (determine if the session rangeLine may also be given here, if it gets parsed once it doesn't need to be parsed again)
                            RtpClient.TransportContext created = null;

                            //If interleaved was present in the response then use a RTP/AVP/TCP Transport
                            if (interleaved)
                            {
                                //If there is a client which is not disposed
                                if (Common.IDisposedExtensions.IsNullOrDisposed(m_RtpClient) is false)
                                {
                                    //Obtain a context via the given data channel or control channel
                                    created = m_RtpClient.GetContextByChannels(dataChannel, controlChannel);

                                    //If the control channel is the same then just update the socket used by the context.
                                    if (Common.IDisposedExtensions.IsNullOrDisposed(created) is false)
                                    {
                                        //created's Rtp and Rtcp Socket could be changed right here...
                                        if (m_RtspSocket.IsNullOrDisposed() is false) created.Initialize(m_RtspSocket, m_RtspSocket);

                                        //Test using a new socket
                                        //if (m_RtspSocket is not null) created.Initialize((IPEndPoint)m_RtspSocket.LocalEndPoint, (IPEndPoint)m_RtspSocket.RemoteEndPoint);

                                        //Store the SessionId on the context.
                                        created.ApplicationContext = SessionId;

                                        session.Context = created;

                                        //Not using the reference anymore
                                        created = null;

                                        return response;
                                    }
                                }

                                //If a context was not already created
                                if (Common.IDisposedExtensions.IsNullOrDisposed(created))
                                {
                                    //Todo, should still be sourceIp...

                                    //Create the context if required.. (Will be created with Sdp Address)
                                    created = RtpClient.TransportContext.FromMediaDescription(SessionDescription, dataChannel, controlChannel, mediaDescription, needsRtcp, remoteSsrc, remoteSsrc != 0 ? 0 : 2, localIp, sourceIp);

                                    //Set the identity to what we indicated to the server.
                                    created.SynchronizationSourceIdentifier = localSsrc;

                                    //Set the minimum packet size
                                    created.MinimumPacketSize = minimumPacketSize;

                                    //Set the maximum packet size
                                    created.MaximumPacketSize = maximumPacketSize;
                                }

                                //If there is not a client
                                if (Common.IDisposedExtensions.IsNullOrDisposed(m_RtpClient))
                                {
                                    //Create a Duplexed reciever using the RtspSocket sharing the RtspClient's buffer's properties
                                    m_RtpClient = new RtpClient(new Common.MemorySegment(m_Buffer));

                                    //Attach an event for interleaved data
                                    m_RtpClient.OutOfBandData += ProcessInterleavedData;
                                }
                                else if (false.Equals(m_RtpProtocol == ProtocolType.Tcp)) goto SetupTcp;

                                //If the source address contains the NAT IpAddress or the source is the same then just use the source.
                                if (IPAddress.Equals(sourceIp, ((IPEndPoint)m_RemoteRtsp).Address) ||
                                    Media.Common.Extensions.IPAddress.IPAddressExtensions.IsOnIntranet(sourceIp))
                                {
                                    //Create from the existing socket (may need reuse port)
                                    created.Initialize(m_RtspSocket, m_RtspSocket);

                                    //Test using a new socket
                                    //if (m_RtspSocket is not null) created.Initialize((IPEndPoint)m_RtspSocket.LocalEndPoint, (IPEndPoint)m_RtspSocket.RemoteEndPoint);

                                    //Don't close this socket when disposing. (The RtpClient will dispose it)
                                    //LeaveOpen = true;
                                }
                                else
                                {
                                    //maybe multicast...

                                    //Create a new socket's
                                    created.Initialize(multicast ? Media.Common.Extensions.Socket.SocketExtensions.GetFirstMulticastIPAddress(sourceIp.AddressFamily) : Media.Common.Extensions.Socket.SocketExtensions.GetFirstUnicastIPAddress(sourceIp.AddressFamily),
                                        sourceIp, serverRtpPort); //Might have to come from source string?

                                    //Handle ttl if present..
                                    if (ttl > 0)
                                    {
                                        created.RtpSocket.Ttl = (short)ttl;

                                        if (needsRtcp && created.RtpSocket.Handle != created.RtcpSocket.Handle) created.RtcpSocket.Ttl = (short)ttl;
                                    }

                                    if (multicast)
                                    {
                                        //Store the joined group
                                        if (created.MulticastGroups.Add(destinationIp))
                                        {
                                            Media.Common.Extensions.Socket.SocketExtensions.JoinMulticastGroup(created.RtpSocket, destinationIp);

                                            Media.Common.Extensions.Socket.SocketExtensions.SetMulticastTimeToLive(created.RtpSocket, ttl);

                                            if (false.Equals(created.RtcpSocket.Handle.Equals(created.RtpSocket.Handle)))
                                            {
                                                Common.Extensions.Socket.SocketExtensions.JoinMulticastGroup(created.RtcpSocket, destinationIp);

                                                Common.Extensions.Socket.SocketExtensions.SetMulticastTimeToLive(created.RtcpSocket, ttl);
                                            }
                                        }
                                    }

                                    //When the RtspClient is disposed that socket will also be disposed.
                                }

                            }
                            else
                            {
                                //The server may respond with the port used for the request which indicates that TCP should be used?
                                if (serverRtpPort.Equals(location.Port)) goto SetupTcp;

                                //If we need to make a client then do so
                                if (Common.IDisposedExtensions.IsNullOrDisposed(m_RtpClient))
                                {
                                    //Create a Udp Reciever sharing the RtspClient's buffer's properties
                                    m_RtpClient = new RtpClient(m_Buffer);

                                    //Attach an event for interleaved data
                                    m_RtpClient.OutOfBandData += ProcessInterleavedData;
                                }
                                else if (Common.IDisposedExtensions.IsNullOrDisposed(created))
                                {
                                    //Obtain the context via the given local or remote id
                                    created = localSsrc is 0 is false ? m_RtpClient.GetContextBySourceId(localSsrc) :
                                            remoteSsrc is 0 is false ? m_RtpClient.GetContextBySourceId(remoteSsrc) : null;

                                    //If the control channel is the same then just update the client and ensure connected.
                                    if (Common.IDisposedExtensions.IsNullOrDisposed(created) is false &&
                                        created.ControlChannel.Equals(controlChannel))
                                    {
                                        created.Initialize(m_RtspSocket);

                                        //m_RtpClient.Connect();

                                        created.ApplicationContext = SessionId;

                                        session.Context = created;

                                        return response;
                                    }
                                }

                                //Get the available context's
                                var availableContexts = m_RtpClient.GetTransportContexts().Where(tc => Common.IDisposedExtensions.IsNullOrDisposed(tc) is false);

                                //OrderBy(c=>c.ControlChannel - c.DataChannel) to get the highest, then would need to determine if at max and could wrap... e.g. getAvailableContextNumber()
                                RtpClient.TransportContext lastContext = availableContexts.LastOrDefault();

                                //Todo, destinationIp should still be sourceIp.

                                created = Common.IDisposedExtensions.IsNullOrDisposed(lastContext) is false
                                    ? RtpClient.TransportContext.FromMediaDescription(SessionDescription, (byte)(lastContext.DataChannel + (multiplexing ? 1 : 2)), (byte)(lastContext.ControlChannel + (multiplexing ? 1 : 2)), mediaDescription, needsRtcp, remoteSsrc, remoteSsrc != 0 ? 0 : 2, localIp, sourceIp)
                                    : RtpClient.TransportContext.FromMediaDescription(SessionDescription, dataChannel, controlChannel, mediaDescription, needsRtcp, remoteSsrc, remoteSsrc != 0 ? 0 : 2, localIp, sourceIp);

                                //Should pass the buffer to Initialize...

                                //Sharing the buffer for tcp, should be an option.... bool shareBuffer and then again on the RtspClient so it can be passed.
                                if (m_RtpProtocol == ProtocolType.Tcp) created.ContextMemory = m_RtpClient.m_Buffer;
                                else created.ContextMemory = new MemorySegment(created.MaximumPacketSize); // maximumPacketSize = 0 here?

                                //Call initialize
                                created.Initialize(localIp, sourceIp, clientRtpPort, clientRtcpPort, serverRtpPort, serverRtcpPort);

                                //Handle ttl if present..
                                if (ttl > 0)
                                {
                                    created.RtpSocket.Ttl = (short)ttl;

                                    if (needsRtcp && created.RtpSocket.Handle != created.RtcpSocket.Handle) created.RtcpSocket.Ttl = (short)ttl;
                                }

                                //Todo, if the desitionIp is not equal to the sourceIp must also be handled (addressFamily also)

                                if (multicast &&
                                    // && false == sourceIp.Equals(destinationIp) 
                                    //&& 
                                    Common.Extensions.IPAddress.IPAddressExtensions.IsMulticast(destinationIp))
                                {
                                    //Todo, try, catch...
                                    Media.Common.Extensions.Socket.SocketExtensions.JoinMulticastGroup(created.RtpSocket, destinationIp);

                                    created.MulticastGroups.Add(destinationIp);

                                    Media.Common.Extensions.Socket.SocketExtensions.SetMulticastTimeToLive(created.RtpSocket, ttl);

                                    if (false.Equals(created.RtcpSocket.Handle == created.RtpSocket.Handle))
                                    {
                                        Common.Extensions.Socket.SocketExtensions.JoinMulticastGroup(created.RtcpSocket, destinationIp);

                                        Common.Extensions.Socket.SocketExtensions.SetMulticastTimeToLive(created.RtcpSocket, ttl);
                                    }
                                }

                                //No longer need the temporary sockets, could probably just have used them in the Context...

                                if (rtpTemp is not null)
                                {
                                    rtpTemp.Dispose();

                                    rtpTemp = null;
                                }

                                if (rtcpTemp is not null)
                                {
                                    rtcpTemp.Dispose();

                                    rtcpTemp = null;
                                }
                            }

                            //if a context was created add it
                            if (created is not null)
                            {
                                m_RtpClient.AddContext(created,
                                    //Check data
                                    multiplexing is false,
                                    //Check control
                                    multiplexing is false);

                                //Store the sessionId in the ApplicationContext.
                                created.ApplicationContext = SessionId;

                                //Store the context in the session
                                session.Context = created;
                            }

                            break;
                        }
                    case SocketError.Shutdown:
                    case SocketError.ConnectionReset:
                    case SocketError.ConnectionAborted:
                    default:
                        {
                            if (Common.IDisposedExtensions.IsNullOrDisposed(this)) return response;

                            if (AutomaticallyReconnect is false) Media.Common.TaggedExceptionExtensions.RaiseTaggedException(this, "Connection Aborted or Reset and AutomaticallyReconnect is false.");

                            Reconnect();

                            //make another request if we didn't already try.
                            if (triedTwoTimes is false)
                            {
                                //Use a new Sequence number
                                setup.RemoveHeader(RtspHeaders.CSeq);

                                //Use a new Timestamp
                                setup.RemoveHeader(RtspHeaders.Timestamp);

                                //Dont try again
                                triedTwoTimes = true;

                                //Wait to clear caches.
                                m_InterleaveEvent.Wait(Common.Extensions.TimeSpan.TimeSpanExtensions.OneTick);

                                goto Setup;
                            }

                            break;
                        }
                }

                #region Unused, Throw more exceptions to realize state.

                //ITC, this could be a badly coded server sending HTTP or otherwise...

                ////if there was no response then don't attempt to parse any but DO attempt to listen.
                //if (triedTwoTimes is false &&
                //    (response is null //The response is null
                //    || //The response is not null and was not actually a response OR the sequenceNumber does not match.
                //    (false.Equals(response.RtspMessageType == RtspMessageType.Response) || response.CSeq < sequenceNumber)))
                //{
                //    if (IsPlaying) Media.Common.TaggedExceptionExtensions.RaiseTaggedException(this, "No response to SETUP." + (false == SupportedMethods.Contains(RtspMethod.SETUP.ToString()) ? " The server may not support SETUP." : string.Empty));
                //    else
                //    {

                //    }
                //}

                #endregion

                //Setup Complete
                return response;
            }
        }
        catch (Exception ex)
        {
            Media.Common.TaggedExceptionExtensions.RaiseTaggedException(this, "Unable to setup media. See InnerException", ex);

            return m_LastTransmitted;
        }

        //Setup for Interleaved connection
        SetupTcp:
        {
            m_RtpProtocol = ProtocolType.Tcp;

            //Recurse call to ensure propper setup
            return SendSetup(location, mediaDescription);
        }
    }

    protected virtual void MonitorProtocol(object state = null)
    {
        //Timer is going away soon enough, TODO, a Thread.

        //Don't reschedule if was removed. (state if passed should be the m_ProtocolMonitor to ensure it's also the same instance)
        if (m_ProtocolMonitor is null) return;

        bool keepAlives = DisableKeepAliveRequest;

        //Should check for KeepAlive header in previous requests?

        //If not already Disposed and the protocol was not already specified as or configured to TCP
        if (Common.IDisposedExtensions.IsNullOrDisposed(this) is false &&  //And
            IsPlaying) //Still playing
        {
            //Filter any context which is not playing, disposed or has activity
            //Todo should have a HasRequiredActivity
            //To quickly fix this I am just checking that nothing was sent or received
            //Notes that when sending only that one ALSO needs to determine `should something still be received` ?

            try
            {
                //Monitor the protocol for incoming messages
                if (SharesSocket is false &&
                    InUse is false &&
                    m_RtspSocket.IsNullOrDisposed() is false &&
                    m_RtspSocket.Poll(m_SocketPollMicroseconds, SelectMode.SelectRead))
                {
                    DisableKeepAliveRequest = true;

                    //Common.ILoggingExtensions.Log(Logger, ToString() + "@MonitorProtocol: Receiving Data");

                    using (var response = SendRtspMessage(null, out SocketError error, out int cseq, false, true, m_MaximumTransactionAttempts))
                    {
                        if (error == SocketError.Success && Common.IDisposedExtensions.IsNullOrDisposed(response) is false) Common.ILoggingExtensions.Log(Logger, ToString() + "@MonitorProtocol: (" + error + ") Received =>" + response.ToString());
                    }

                    //Common.ILoggingExtensions.Log(Logger, ToString() + "@MonitorProtocol: Data Received");

                    DisableKeepAliveRequest = keepAlives;
                }
            }
            catch (Exception ex)
            {
                Common.ILoggingExtensions.Log(Logger, ToString() + "@MonitorProtocol: " + ex.Message);
                Common.ILoggingExtensions.LogException(Logger, ex);
            }

            //If not disposed AND IsConnected and if protocol switch is still allowed AND IsPlaying and not already TCP
            if (Common.IDisposedExtensions.IsNullOrDisposed(this) is false &&
                IsConnected &&
                m_RtpProtocol is not ProtocolType.Tcp &&
                AllowAlternateTransport &&
                IsPlaying)
            {
                //Filter the contexts which have received absolutely NO data.
                var contextsWithoutFlow = Client.GetTransportContexts().Where(tc => Common.IDisposedExtensions.IsNullOrDisposed(tc) is false &&
                    m_Playing.ContainsKey(tc.MediaDescription) &&
                    tc.TotalBytesReceieved is Common.Binary.LongZero &&
                    tc.TotalPacketsSent is Common.Binary.LongZero &&
                    tc.TimeActive > tc.ReceiveInterval);
                //tc.TimeSending > tc.ReceiveInterval);

                //InactiveTime or ActiveTime on the tc. (Another value)

                //If there are any context's which are not flowing but are playing
                if (contextsWithoutFlow.Count() >= m_Playing.Count)// and the amount of them is greater than or equal to what the rtsp client is playing
                {
                    try
                    {
                        //If the client has not already switched to Tcp
                        if (m_RtpProtocol is not ProtocolType.Tcp)
                        {
                            //Ensure Tcp protocol
                            m_RtpProtocol = ProtocolType.Tcp;
                        }
                        else if (m_RtpProtocol is not ProtocolType.Udp)
                        {
                            //Ensure Udp protocol
                            m_RtpProtocol = ProtocolType.Udp;
                        }
                        else
                        {
                            //Ensure IP protocol
                            m_RtpProtocol = ProtocolType.IP;
                        }

                        //Stop sending them for now
                        if (keepAlives is false) DisableKeepAliveRequest = true;

                        //Wait for any existing request to complete
                        while (InUse) m_InterleaveEvent.Wait(Common.Extensions.TimeSpan.TimeSpanExtensions.OneTick);

                        Common.ILoggingExtensions.Log(Logger, ToString() + "@MonitorProtocol: StopPlaying");

                        //Stop all playback
                        StopPlaying(false);

                        //If this is not done then when StartPlaying is called again 
                        //SendSetup will may find a Context which exists with the same ssrc.
                        //It should be determined then if the context can be updated or not with the new socket.
                        //It would only save a small amount of memory
                        m_RtpClient.DisposeAndClearTransportContexts();

                        //Cache
                        while (IsPlaying || InUse)
                        {
                            Common.ILoggingExtensions.Log(Logger, ToString() + "@MonitorProtocol: Waiting for IsPlaying to be false.");

                            if (m_InterleaveEvent.Wait(Common.Extensions.TimeSpan.TimeSpanExtensions.OneTick))
                            {
                                Common.ILoggingExtensions.Log(Logger, ToString() + "@MonitorProtocol: Saw signal.");
                            }
                        }

                        Common.ILoggingExtensions.Log(Logger, ToString() + "@MonitorProtocol: StartPlaying");

                        //Start again
                        StartPlaying();

                        //Restore value
                        DisableKeepAliveRequest = keepAlives;
                    }
                    catch (Exception ex)
                    {
                        Common.ILoggingExtensions.Log(Logger, ToString() + "@MonitorProtocol: " + ex.Message);
                        Common.ILoggingExtensions.LogException(Logger, ex);
                    }
                }
            }
        }

        ////If playing then check for context without flow..
        //if(m_Playing.Count > 0)
        //{
        //    //Filter the contexts which have received no data with in the recieve interval.
        //    var contextsWithoutFlow = Client.GetTransportContexts().Where(tc => Common.IDisposedExtensions.IsNullOrDisposed(tc) is false &&
        //        m_Playing.Keys.Contains(tc.MediaDescription) &&
        //        tc.HasReceivedRtpWithinReceiveInterval is false &&
        //        tc.TimeActive > tc.ReceiveInterval);

        //    if (contextsWithoutFlow.Any())
        //    {
        //        StopPlaying();
        //        StartPlaying();
        //    }
        //}

        //If there is still a timer change it based on the last messages round trip time, should be relative to all messages...
        if (Common.IDisposedExtensions.IsNullOrDisposed(this) is false && m_ProtocolMonitor is not null)
            try
            {
                m_ProtocolMonitor.Change
                (
                    m_ConnectionTime.Add(LastMessageRoundTripTime),
                    Media.Common.Extensions.TimeSpan.TimeSpanExtensions.InfiniteTimeSpan
                );
            }
            catch (Exception ex)
            {
                Common.ILoggingExtensions.Log(Logger, ToString() + "@MonitorProtocol: " + ex.Message);
                Common.ILoggingExtensions.LogException(Logger, ex);
            }
    }

    public RtspMessage SendPlay(MediaDescription mediaDescription, TimeSpan? startTime = null, TimeSpan? endTime = null, string rangeType = "npt")
    {
        if (Common.IDisposedExtensions.IsNullOrDisposed(mediaDescription)) throw new ArgumentNullException("mediaDescription");

        //Check if the session supports pausing a specific media item
        if (SessionDescription.SupportsAggregateMediaControl(CurrentLocation) is false) throw new InvalidOperationException("The SessionDescription does not allow aggregate control of the CurrentLocation.");

        RtpClient.TransportContext context = Client.GetContextForMediaDescription(mediaDescription);

        if (Common.IDisposedExtensions.IsNullOrDisposed(context)) Media.Common.TaggedExceptionExtensions.RaiseTaggedException(context, "The given mediaDescription has not been SETUP or is disposed. See Tag for context.");

        //Check if the media was previsouly playing
        if (Common.IDisposedExtensions.IsNullOrDisposed(context) is false &&
            Common.IDisposedExtensions.IsNullOrDisposed(mediaDescription) is false &&
            false.Equals(m_Playing.ContainsKey(mediaDescription)))
        {
            //Keep track of whats playing
            m_Playing.Add(mediaDescription, new MediaSessionState(SessionDescription, mediaDescription));

            //Raise an event now.
            OnPlaying(mediaDescription);
        }

        //Send the play request
        return SendPlay(mediaDescription.GetAbsoluteControlUri(CurrentLocation, SessionDescription), startTime ?? context.MediaStartTime, endTime ?? context.MediaEndTime, rangeType);
    }

    public RtspMessage SendPlay(Uri location = null, TimeSpan? startTime = null, TimeSpan? endTime = null, string rangeType = "npt", bool force = false)
    {
        return SendPlay(out int sequenceNumber, location, startTime, endTime, rangeType, force);
    }

    public RtspMessage SendPlay(out int sequenceNumber, Uri location = null, TimeSpan? startTime = null, TimeSpan? endTime = null, string rangeType = "npt", bool force = false)
    {
        //CheckDisposed?

        sequenceNumber = -1;

        //If not forced
        if (force is false)
        {
            //Usually at least setup must occur so we must have sent and received a setup to actually play
            force = m_ReceivedMessages > 0 && SupportedMethods.Contains(RtspMethod.SETUP.ToString());

            //If not forced and the soure does not support play then throw an exception
            if (force is false &&
                SupportedMethods.Count > 0 &&  //There are some methods supported
                false.Equals(SupportedMethods.Contains(RtspMethod.PLAY.ToString()))) throw new InvalidOperationException("Server does not support PLAY.");
        }

        //Check that the Timing description of the session description allows play?           

        using (RtspMessage play = new(RtspMessageType.Request)
        {
            RtspMethod = RtspMethod.PLAY,
            Location = location ?? m_CurrentLocation
        })
        {
            /*
                A PLAY request without a Range header is legal. It starts playing a
                stream from the beginning unless the stream has been paused. If a
                stream has been paused via PAUSE, stream delivery resumes at the
                pause point. If a stream is playing, such a PLAY request causes no
                further action and can be used by the client to test server liveness.
                */

            //Maybe should not be set if no start or end time is given.
            if (startTime.HasValue || endTime.HasValue) play.SetHeader(RtspHeaders.Range, RtspHeaders.RangeHeader(startTime, endTime, rangeType));
            else if (string.IsNullOrWhiteSpace(rangeType) is false) //otherwise is a non null or whitespace string was given for rangeType
            {
                //Use the given rangeType string verbtaim.
                play.SetHeader(RtspHeaders.Range, rangeType);
            }


            //If CloseConnection was specified and the message does not already contain a Connection header
            if (AutomaticallyDisconnectAfterStartPlaying && play.ContainsHeader(RtspHeaders.Connection) is false)
            {
                //Set the Connection header to close.
                play.AppendOrSetHeader(RtspHeaders.Connection, RtspHeaderFields.Connection.Close);
            }

            //Store any error

            //Send the response
            RtspMessage response = SendRtspMessage(play, out SocketError error, out sequenceNumber, true, true, m_MaximumTransactionAttempts); //?? m_LastTransmitted;

            //response may be null because the server dropped the response due to an invalid header on the request.

            //Handle allowed problems with reception of the play response if already playing
            if (error is SocketError.Success && IsPlaying is false && Common.IDisposedExtensions.IsNullOrDisposed(this) is false && IsConnected)
            {
                //No response or invalid range.
                if (response is null || response.RtspStatusCode is RtspStatusCode.InvalidRange)
                {
                    //if (response == null && m_RtpProtocol == ProtocolType.Tcp)
                    //{
                    //    //If there is transport
                    //    if (false == Common.IDisposedExtensions.IsNullOrDisposed(m_RtpClient))
                    //    {
                    //        //Connect the client now.
                    //        m_RtpClient.Activate();
                    //    }

                    //    return response;
                    //}

                    play.RemoveHeader(Rtsp.RtspHeaders.Range);

                    play.RemoveHeader(Rtsp.RtspHeaders.CSeq);

                    play.RemoveHeader(RtspHeaders.Timestamp);

                    return SendRtspMessage(play);
                }
                else if (response.RtspStatusCode <= RtspStatusCode.OK)
                {
                    //If there is transport
                    if (Common.IDisposedExtensions.IsNullOrDisposed(m_RtpClient) is false)
                    {
                        //Connect the client now.
                        m_RtpClient.Activate();
                    }

                    //Set EndTime based on Range

                    //string rangeHeader = response[RtspHeaders.Range];

                    //Should really only get the RtpInfo header if its needed....

                    //Get the rtp-info header
                    string rtpInfo = response[RtspHeaders.RtpInfo];


                    //Make a parser class which can be reused?

                    //If parsing of the header succeeded
                    if (RtspHeaders.TryParseRtpInfo(rtpInfo, out string[] rtpInfos))
                    {
                        //Notes that more then 1 value here indicates AggregateControl is supported at the server but possibly not the session?

                        //Loop all found sub header values
                        foreach (string rtpInfoValue in rtpInfos)
                        {




                            //If any value which was needed was found.
                            if (RtspHeaders.TryParseRtpInfo(rtpInfoValue, out Uri uri, out int? seq, out int? rtpTime, out int? ssrc))
                            {
                                //Just use the ssrc to lookup the context.
                                if (ssrc.HasValue)
                                {
                                    //Get the context created with the ssrc defined above
                                    RtpClient.TransportContext context = m_RtpClient.GetContextBySourceId(ssrc.Value);

                                    //If that context is not null then allow it's ssrc to change now.
                                    if (Common.IDisposedExtensions.IsNullOrDisposed(context) is false)
                                    {
                                        context.RemoteSynchronizationSourceIdentifier = ssrc.Value;

                                        if (seq.HasValue) context.RecieveSequenceNumber = seq.Value;

                                        if (rtpTime.HasValue) context.RtpTimestamp = rtpTime.Value;

                                        //if (context.Goodbye is not null) context.Goodbye = null;

                                        context = null;
                                    }
                                }
                                else if (uri is not null)
                                {
                                    //Need to get the context by the uri.
                                    //Location = rtsp://abc.com/live/movie
                                    //uri = rtsp://abc.com/live/movie/trackId=0
                                    //uri = rtsp://abc.com/live/movie/trackId=1
                                    //uri = rtsp://abc.com/live/movie/trackId=2

                                    //Get the context created with from the media description with the same resulting control uri
                                    RtpClient.TransportContext context = m_RtpClient.GetTransportContexts().FirstOrDefault(tc => tc.MediaDescription.GetAbsoluteControlUri(CurrentLocation, SessionDescription) == uri);

                                    //If that context is not null then allow it's ssrc to change now.
                                    if (Common.IDisposedExtensions.IsNullOrDisposed(context) is false)
                                    {
                                        if (ssrc.HasValue) context.RemoteSynchronizationSourceIdentifier = ssrc.Value;

                                        if (seq.HasValue) context.RecieveSequenceNumber = seq.Value;

                                        if (rtpTime.HasValue) context.RtpTimestamp = rtpTime.Value;

                                        //if (context.Goodbye is not null) context.Goodbye = null;

                                        context = null;
                                    }

                                }
                            }
                        }
                    }
                }
            }

            //The CloseConnection was specified and the response was received
            if (AutomaticallyDisconnectAfterStartPlaying && Common.IDisposedExtensions.IsNullOrDisposed(response) is false)
            {
                //Should also check if response was seen that it has closed the servers connection...

                //Disconnect the socket.
                DisconnectSocket();
            }

            return response;
        }
    }

    /// <summary>
    /// Sends a PAUSE Request
    /// </summary>
    /// <param name="location">The location to indicate in the request</param>
    /// <returns>The response</returns>
    public RtspMessage SendPause(MediaDescription mediaDescription = null, bool force = false)
    {
        return SendPause(out int cseq, mediaDescription, force);
    }

    public RtspMessage SendPause(out int sequenceNumber, MediaDescription mediaDescription = null, bool force = false)
    {
        //Ensure media has been setup unless forced.
        if (Common.IDisposedExtensions.IsNullOrDisposed(mediaDescription) is false && force is false)
        {
            //Check if the session supports pausing a specific media item
            if (SessionDescription.SupportsAggregateMediaControl(CurrentLocation) is false) throw new InvalidOperationException("The SessionDescription does not allow aggregate control.");

            //Get a context for the media
            var context = Client.GetContextForMediaDescription(mediaDescription);

            //If there is no context then throw an exception.
            if (Common.IDisposedExtensions.IsNullOrDisposed(context)) throw new InvalidOperationException("The given mediaDescription has not been SETUP.");

            context = null;
        }

        //Keep track of whats playing
        if (Common.IDisposedExtensions.IsNullOrDisposed(mediaDescription)) m_Playing.Clear();
        else m_Playing.Remove(mediaDescription);

        //Fire the event now
        OnPausing(mediaDescription);

        //Send the pause request, determining if the request is for all media or just one.
        return SendPause(out sequenceNumber, Common.IDisposedExtensions.IsNullOrDisposed(mediaDescription) ? CurrentLocation : mediaDescription.GetAbsoluteControlUri(CurrentLocation, SessionDescription), force);
    }

    public RtspMessage SendPause(out int sequenceNumber, Uri location = null, bool force = false)
    {
        //If the server doesn't support it
        if (SupportedMethods.Contains(RtspMethod.PAUSE.ToString()) is false && force is false) throw new InvalidOperationException("Server does not support PAUSE.");

        //if (!Playing) throw new InvalidOperationException("RtspClient is not Playing.");
        using (RtspMessage pause = new(RtspMessageType.Request)
        {
            RtspMethod = RtspMethod.PAUSE,
            Location = location ?? m_CurrentLocation
        })
        {
            return SendRtspMessage(pause, out SocketError error, out sequenceNumber, true, true, m_MaximumTransactionAttempts);
        }
    }

    /// <summary>
    /// Sends a ANNOUNCE Request
    /// </summary>
    /// <param name="location">The location to indicate in the request, otherwise null to use the <see cref="CurrentLocation"/></param>
    /// <param name="sdp">The <see cref="SessionDescription"/> to ANNOUNCE</param>
    /// <returns>The response</returns>
    public RtspMessage SendAnnounce(Uri location, SessionDescription sdp, bool force = false)
    {
        if (false == SupportedMethods.Contains(RtspMethod.ANNOUNCE.ToString()) && false == force) throw new InvalidOperationException("Server does not support ANNOUNCE.");
        if (Common.IDisposedExtensions.IsNullOrDisposed(sdp)) throw new InvalidOperationException("sdp is null or disposed");
        using (RtspMessage announce = new(RtspMessageType.Request)
        {
            RtspMethod = RtspMethod.ANNOUNCE,
            Location = location ?? m_CurrentLocation
        })
        {
            announce.Body = sdp.ToString();
            announce.SetHeader(RtspHeaders.ContentType, Sdp.SessionDescription.MimeType);
            return SendRtspMessage(announce);
        }
    }

    //SendRecord

    internal void SendKeepAliveRequest(object state)
    {
        bool wasPlaying = false, wasConnected = false;

        try
        {
            //Thrown an exception if IsDisposed or add force parameter and check that with keep alive timer, e.g. force is false &&
            if (Common.IDisposedExtensions.IsNullOrDisposed(this) || m_KeepAliveTimer is null) return;

            wasPlaying = IsPlaying;

            //Save the state of the connection
            wasConnected = IsConnected;

            //If the keep alive request feature is not disabled and the session times out if not kept alive
            if (wasPlaying && IsPlaying &&
                DisableKeepAliveRequest is false &&
                m_RtspSessionTimeout > TimeSpan.Zero)
            {
                //Don't send a keep alive if the stream is ending before the next keep alive would be sent.
                if (EndTime.HasValue && EndTime.Value != Media.Common.Extensions.TimeSpan.TimeSpanExtensions.InfiniteTimeSpan &&
                    EndTime.Value - ((DateTime.UtcNow - m_StartedPlaying.Value)) <= m_RtspSessionTimeout) return;

                //Ensure transport is connected. (will be done in play...)
                //if (false == m_RtpClient.IsConnected) m_RtpClient.Connect();

                while (InUse) m_InterleaveEvent.Wait(Common.Extensions.TimeSpan.TimeSpanExtensions.OneTick);

                DisableKeepAliveRequest = true;

                foreach (var session in m_Sessions)
                {
                    //If the session itself doesn't support keep alive then continue.
                    if (session.Value.EnableKeepAliveRequest is false) continue;

                    //Using the SessionId 
                    //session.Key

                    //perform the request

                    //Note, that each session may use a totally different connection.

                    //Check if GET_PARAMETER is supported.
                    if (SupportedMethods.Contains(RtspMethod.GET_PARAMETER.ToString()))
                    {
                        //Need the message itself to update the session..
                        using (SendGetParameter(null, null, session.Value.SessionId, RtspHeaderFields.Connection.KeepAlive, false)) ;
                    }
                    else if (SupportedMethods.Contains(RtspMethod.OPTIONS.ToString())) //If at least options is supported
                    {
                        using (SendOptions(session.Value.ControlLocation == RtspMessage.Wildcard, session.Value.SessionId, RtspHeaderFields.Connection.KeepAlive)) ;
                    }
                    else if (SupportedMethods.Contains(RtspMethod.PLAY.ToString())) //If at least PLAY is supported
                    {
                        using (SendPlay()) ; //Sessionid overload
                    }

                }


                DisableKeepAliveRequest = false;
            }

            //Should be done in Monitor Protocol

            //Only perform these actions if playing anything.
            if (wasPlaying)
            {
                //Raise events for ended media.
                for (int i = 0; i < Client.TransportContexts.Count; ++i)
                {
                    var context = Client.TransportContexts[i];

                    if (Common.IDisposedExtensions.IsNullOrDisposed(context) || context.IsContinious || context.TimeReceiving < context.MediaEndTime) continue;

                    //Remove from the playing media and if it was contained raise an event.
                    if (m_Playing.Remove(context.MediaDescription)) OnStopping(context.MediaDescription);
                }

                bool aggregateControl = SessionDescription.SupportsAggregateMediaControl(CurrentLocation);

                foreach (var kvp in m_Playing)
                {
                    switch (kvp.Value.Status)
                    {
                        case MediaStatus.Unknown:
                        case MediaStatus.Error:
                        case MediaStatus.Stopped:
                        case MediaStatus.Paused:
                        case MediaStatus.Seeking:
                        case MediaStatus.Encrypting:
                        case MediaStatus.Decrypting:
                            continue;
                    }

                    var mediaDescription = kvp.Key;

                    //Get a context
                    var context = Client.GetContextForMediaDescription(mediaDescription);

                    //If there is a context ensure it has not ended and has received data within the context receive interval.
                    if (Common.IDisposedExtensions.IsNullOrDisposed(context) ||
                        context.Goodbye is null ||
                        context.IsContinious ||
                        context.TimeSending < context.MediaEndTime) continue;

                    //Teardown the media if the session supports AggregateControl
                    //(Todo, Each context may have it's own sessionId)
                    //Also the Server may have already stopped the media...
                    if (aggregateControl) using (SendTeardown(mediaDescription, true)) ;
                    else if (m_Playing.Remove(mediaDescription))
                    {//Otherwise Remove from the playing media and if it was contained raise an event.
                        OnStopping(mediaDescription);
                        kvp.Value.Status = MediaStatus.Stopped;
                    }

                    //If there was a context for the media ensure it is removed and disposed from the underlying transport.
                    if (Common.IDisposedExtensions.IsNullOrDisposed(context) is false)
                    {
                        //handle leaving open changes?
                        //if(context.LeaveOpen)

                        //Remove the context from the rtp client.
                        Client.TryRemoveContext(context);

                        //Dispose of it.
                        context.Dispose();

                        //Remove any reference
                        context = null;
                    }
                }

                //Ensure media is still flowing if still playing otherwise raise the stopping event.
                if (IsPlaying) EnsureMediaFlows();
                else if (wasPlaying) OnStopping(); //Ensure not already raised?
            }

            //Determine next time to send a keep alive
            if (m_KeepAliveTimer is not null && IsPlaying)
            {
                //Todo, Check if the media will end before the next keep alive is due before sending.
                if (m_LastMessageRoundTripTime < m_RtspSessionTimeout) m_KeepAliveTimer.Change(TimeSpan.FromTicks(m_RtspSessionTimeout.Subtract(m_LastMessageRoundTripTime + m_ConnectionTime).Duration().Ticks / 2), Media.Common.Extensions.TimeSpan.TimeSpanExtensions.InfiniteTimeSpan);
            }

        }
        catch (Exception ex)
        {
            Common.ILoggingExtensions.Log(Logger, ToString() + "@SendKeepAlive: " + ex.Message);
            Common.ILoggingExtensions.LogException(Logger, ex);
        }

        //Raise the stopping event if not playing anymore
        //if (true == wasPlaying && false == IsPlaying) OnStopping();

        //Deactivate if was previously disconnected so long as the ProtocolSwitchTimer is not activated.
        //Might need a flag to see if DisconnectSocket was called.
        //if (m_ProtocolSwitchTimer is null && false == wasConnected && IsPlaying && true == IsConnected) DisconnectSocket();
    }

    public void EnsureMediaFlows()
    {

        if (InUse) return;

        DisableKeepAliveRequest = true;

        //If not waiting to switch protocols
        if (m_ProtocolMonitor is null && IsPlaying)
        {

            //If not playing anymore do nothing
            if (EndTime != Media.Common.Extensions.TimeSpan.TimeSpanExtensions.InfiniteTimeSpan &&
                DateTime.UtcNow - m_StartedPlaying.Value > EndTime)
            {
                StopPlaying();

                return;
            }

            //Determine what contexts are playing and have set a goodbye
            var contextsWithGoodbye = Client.GetTransportContexts().Where(tc => Common.IDisposedExtensions.IsNullOrDisposed(tc) is false &&
                m_Playing.ContainsKey(tc.MediaDescription) &&
                tc.Goodbye is not null);

            //If there are such contexts
            if (m_InterleaveEvent.IsSet && IsPlaying && contextsWithGoodbye.Any())
            {
                //If the server doens't support pause then we cant pause.
                bool supportPause = SupportedMethods.Contains(RtspMethod.PAUSE.ToString());

                //If any media was pausedOrStopped.
                bool pausedOrStoppedAnything = false;

                //If we cannot stop a single media item we will set this to true.
                bool stopAll = SessionDescription.SupportsAggregateMediaControl(CurrentLocation) is false;

                //Iterate all inactive contexts.
                if (stopAll is false) foreach (var context in contextsWithGoodbye.ToArray())
                    {
                        //Ensure still in playing
                        if (Common.IDisposedExtensions.IsNullOrDisposed(context) is false &&
                            m_Playing.ContainsKey(context.MediaDescription) is false || context.HasAnyRecentActivity) continue;

                        //Send a pause request if not already paused and the server supports PAUSE and there has been any activity on the context
                        if (supportPause)
                        {
                            //If not going to be playing anymore do nothing
                            if (context.TimeRemaining >= context.ReceiveInterval + m_LastMessageRoundTripTime + m_LastServerDelay) continue;

                            //If the context is not continious and there is no more time remaining do nothing
                            if (context.IsContinious is false && context.TimeRemaining <= TimeSpan.Zero) continue;

                            //Send the PAUSE request
                            using (var pauseResponse = SendPause(out int requestCseq, context.MediaDescription))
                            {

                                //If the paused request was not a sucess then it's probably due to an aggregate operation
                                //Determine if we have to stop everything.
                                if (pauseResponse is null || pauseResponse.RtspStatusCode <= RtspStatusCode.OK)
                                {

                                    //Sometime the server doesn't respond to pause or the response gets lost.
                                    if (pauseResponse is null || pauseResponse.RtspMessageType == RtspMessageType.Invalid)
                                    {
                                        //Wait up until the time another request is sent.
                                        m_InterleaveEvent.Wait(m_RtspSessionTimeout);

                                        //Need a way to be able to check the request's sequence number..

                                        //if(m_LastTransmitted is not null && m_LastTransmitted.CSeq == pauseReqeust.SequnceNumber)

                                        stopAll = true;
                                    }
                                    else   //See if everything has to be stopped.
                                        stopAll = pauseResponse.RtspStatusCode == RtspStatusCode.AggregateOpperationNotAllowed;

                                    //If the request failed then nothing was paused yet
                                    pausedOrStoppedAnything = false;

                                    //Could move this logic to the SendPause method which would check the response status code before returning the response and then wouldn't raise the Pause event.

                                    //Ensure external state is observed
                                    m_Playing.Add(context.MediaDescription, new MediaSessionState(SessionDescription, context.MediaDescription));

                                    OnPlaying(context.MediaDescription);
                                }
                            }
                        }
                        else
                        {
                            //If not going to be playing anymore do nothing
                            if (context.TimeRemaining >= context.ReceiveInterval + m_LastMessageRoundTripTime + m_LastServerDelay) continue;

                            //If the context is not continious and there is no more time remaining do nothing
                            if (context.IsContinious is false && context.TimeRemaining <= TimeSpan.Zero) continue;

                            //If the context was disposed then continue
                            if (Common.IDisposedExtensions.IsNullOrDisposed(context)) continue;

                            //We can't pause so STOP JUST THIS MEDIA
                            using (var teardownResponse = SendTeardown(context.MediaDescription))
                            {
                                //If the Teardown was not a success then it's probably due to an aggregate operation.
                                //If the paused request was not a sucess then it's probably due to an aggregate operation
                                //Determine if we have to stop everything.
                                if (teardownResponse is null || teardownResponse.RtspStatusCode <= RtspStatusCode.OK)
                                {
                                    //Sometime the server doesn't respond to pause or the response gets lost.
                                    if (teardownResponse is null || teardownResponse.RtspMessageType == RtspMessageType.Invalid)
                                    {
                                        //Wait up until the time another request is sent.
                                        m_InterleaveEvent.Wait(m_RtspSessionTimeout);

                                        //Need a way to be able to check the request's sequence number..

                                        //if(m_LastTransmitted is not null && m_LastTransmitted.CSeq == pauseReqeust.SequnceNumber)

                                        stopAll = true;
                                    }
                                    else   //See if everything has to be stopped.
                                        stopAll = teardownResponse.RtspStatusCode == RtspStatusCode.AggregateOpperationNotAllowed;

                                    //If the request failed then nothing was paused yet
                                    pausedOrStoppedAnything = false;

                                    //Could move this logic to the SendPause method which would check the response status code before returning the response and then wouldn't raise the Pause event.

                                    //Ensure external state is observed
                                    m_Playing.Add(context.MediaDescription, new MediaSessionState(SessionDescription, context.MediaDescription));

                                    OnPlaying(context.MediaDescription);

                                }
                            }
                        }

                        //If we have to stop everything and the server doesn't support pause then stop iterating.
                        if (stopAll) break;

                        //The media was paused or stopped, so play it again if anything was received
                        if (pausedOrStoppedAnything && context.TotalBytesReceieved > 0)
                        {
                            //context.Goodbye.Dispose();

                            //Ensure the context state allows for sending again.
                            context.Goodbye = null;

                            //Try to play the media again
                            try { Play(context.MediaDescription); }
                            catch
                            {
                                //Ensure external state is observed, the media is still playing
                                m_Playing.Add(context.MediaDescription, new MediaSessionState(SessionDescription, context.MediaDescription));

                                OnPlaying(context.MediaDescription);
                            }
                        }
                    }

                //If everything needs to stop.
                if (stopAll && IsPlaying &&
                    EndTime.HasValue &&
                    EndTime.Value.Equals(Media.Common.Extensions.TimeSpan.TimeSpanExtensions.InfiniteTimeSpan) is false &&
                    //And there is enough time to attempt
                    DateTime.UtcNow - m_StartedPlaying.Value > EndTime.Value.Subtract(m_LastMessageRoundTripTime.Add(m_ConnectionTime.Add(m_LastServerDelay)))
                    && contextsWithGoodbye.All(tc => Common.IDisposedExtensions.IsNullOrDisposed(tc) is false && tc.HasAnyRecentActivity is false))
                {

                    if (supportPause)
                    {
                        //Pause all media
                        Pause();

                        //Start playing again
                        StartPlaying();
                    }
                    else
                    {
                        //If still connected
                        if (IsConnected)
                        {
                            //Just send a play to continue receiving whatever media is still sending.
                            using (SendPlay()) ;

                        }
                        else
                        {
                            //Stop playing everything
                            StopPlaying();

                            //Start playing everything
                            StartPlaying();
                        }
                    }
                }
            }
        }

        DisableKeepAliveRequest = false;
    }

    public RtspMessage SendGetParameter(string body = null, string contentType = null, string sessionId = null, string connection = null, bool force = false)
    {
        //…Content-type: application/x-rtsp-packetpair for WMS

        //If the server doesn't support it
        if (false.Equals(SupportedMethods.Contains(RtspMethod.GET_PARAMETER.ToString())) && force is false) throw new InvalidOperationException("Server does not support GET_PARAMETER.");

        //Need a session id
        using (RtspMessage get = new(RtspMessageType.Request)
        {
            RtspMethod = RtspMethod.GET_PARAMETER,
            Location = CurrentLocation,
            Body = body ?? string.Empty
        })
        {
            if (string.IsNullOrWhiteSpace(contentType) is false) get.SetHeader(RtspHeaders.ContentType, contentType);

            if (string.IsNullOrWhiteSpace(sessionId) is false) get.SetHeader(RtspHeaders.Session, sessionId);

            if (string.IsNullOrWhiteSpace(connection) is false) get.SetHeader(RtspHeaders.Connection, connection);

            return SendRtspMessage(get);
        }
    }

    public RtspMessage SendSetParameter(string body = null, string contentType = null, string sessionId = null, bool force = false)
    {
        //If the server doesn't support it
        if (false == SupportedMethods.Contains(RtspMethod.SET_PARAMETER.ToString()) && false == force) throw new InvalidOperationException("Server does not support GET_PARAMETER.");

        using (RtspMessage set = new(RtspMessageType.Request)
        {
            RtspMethod = RtspMethod.SET_PARAMETER,
            Location = CurrentLocation,
            Body = body ?? string.Empty
        })
        {
            if (string.IsNullOrWhiteSpace(contentType) is false) set.SetHeader(RtspHeaders.ContentType, contentType);

            if (string.IsNullOrWhiteSpace(sessionId) is false) set.SetHeader(RtspHeaders.Session, sessionId);

            return SendRtspMessage(set);
        }
    }

    #endregion

    #region Overloads

    public override string ToString()
    {
        return string.Join(((char)Common.ASCII.HyphenSign).ToString(), base.ToString(), InternalId);
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Stops sending any Keep Alive Immediately and calls <see cref="StopPlaying"/>.
    /// If the <see cref="RtpClient"/> is not null:
    /// Removes the <see cref="ProcessInterleavedData"/> event
    /// Disposes the RtpClient and sets it to null.
    /// Disposes and sets the Buffer to null.
    /// Disposes and sets the InterleavedEvent to null.
    /// Disposes and sets the m_LastTransmitted to null.
    /// Disposes and sets the <see cref="RtspSocket"/> to null if <see cref="LeaveOpen"/> allows.
    /// Removes connection times so <see cref="IsConnected"/> is false.
    /// Stops raising any events.
    /// Removes any <see cref="Logger"/>
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing is false || ShouldDispose is false) return;

        base.Dispose(ShouldDispose);

        if (Common.IDisposedExtensions.IsNullOrDisposed(this) is false) return;

        if (m_ProtocolMonitor is not null)
        {
            m_ProtocolMonitor.Dispose();

            m_ProtocolMonitor = null;
        }

        DisableKeepAliveRequest = true;

        StopPlaying();

        if (Common.IDisposedExtensions.IsNullOrDisposed(m_RtpClient) is false)
        {
            m_RtpClient.OutOfBandData -= ProcessInterleavedData;

            m_RtpClient.Dispose();

            m_RtpClient = null;
        }

        if (Common.IDisposedExtensions.IsNullOrDisposed(m_Buffer) is false)
        {
            m_Buffer.Dispose();

            m_Buffer = null;
        }

        if (Common.IDisposedExtensions.IsNullOrDisposed(m_LastTransmitted) is false)
        {
            m_LastTransmitted.Dispose();

            m_LastTransmitted = null;
        }

        m_InterleaveEvent?.Dispose();

        if (m_RtspSocket is not null)
        {
            if (LeaveOpen is false) m_RtspSocket.Dispose();

            m_RtspSocket = null;
        }

        if (Common.IDisposedExtensions.IsNullOrDisposed(m_SessionDescription) is false)
        {
            m_SessionDescription.Dispose();

            m_SessionDescription = null;
        }

        m_AuthorizationHeader = null;

        m_BeginConnect = m_EndConnect = null;

        OnConnect = null;
        OnDisconnect = null;
        OnStop = null;
        OnPlay = null;
        OnPause = null;
        OnRequest = null;
        OnResponse = null;

        Logger = null;
    }

    #endregion

    IEnumerable<Socket> Common.ISocketReference.GetReferencedSockets()
    {
        if (Common.IDisposedExtensions.IsNullOrDisposed(this)) yield break;

        //Technically any sockets which are used by a session should also be returned.

        yield return m_RtspSocket;
    }
}
