﻿/*
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
using Media.Common.Extensions.Socket;
using System;
using System.Linq;

namespace Media.Rtp
{
    /// <summary>
    /// The fields of a <see cref="RtpClient"/> instance.
    /// </summary>
    public partial class RtpClient : Media.Common.ILoggingReference
    {
        #region Methods

        /// <summary>
        /// Adds a the given context to the instances owned by this client. 
        /// Throws a RtpClientException if the given context conflicts in channel either data or control with that of one which is already owned by the instance.
        /// </summary>
        /// <param name="context">The context to add</param>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public /*virtual*/ void AddContext(TransportContext context, bool checkDataChannel = true, bool checkControlChannel = true, bool checkLocalIdentity = true, bool checkRemoteIdentity = true)
        {
            if (checkDataChannel || checkControlChannel || checkLocalIdentity || checkRemoteIdentity) foreach (TransportContext c in TransportContexts)
                {
                    //If checking channels
                    if (checkDataChannel || checkControlChannel)
                    {
                        //If checking the data channel
                        if (checkDataChannel && c.DataChannel == context.DataChannel || c.ControlChannel == context.DataChannel)
                        {
                            Media.Common.TaggedExceptionExtensions.RaiseTaggedException(c, "Requested Data Channel is already in use by the context in the Tag");
                        }

                        //if checking the control channel
                        if (checkControlChannel && c.ControlChannel == context.ControlChannel || c.DataChannel == context.ControlChannel)
                        {
                            Media.Common.TaggedExceptionExtensions.RaiseTaggedException(c, "Requested Control Channel is already in use by the context in the Tag");
                        }

                    }

                    //If the identity will overlap the Payload type CANNOT be the same

                    //if chekcking local identifier
                    if (checkLocalIdentity && c.SynchronizationSourceIdentifier == context.SynchronizationSourceIdentifier)
                    {
                        foreach (var pt in context.MediaDescription.PayloadTypes)
                        {
                            if (System.Linq.Enumerable.Contains(c.MediaDescription.PayloadTypes, pt))
                            {
                                Media.Common.TaggedExceptionExtensions.RaiseTaggedException(c, "Requested Local SSRC is already in use by the context in the Tag");
                            }
                        }
                    }

                    //if chekcking remote identifier (and it has been defined)
                    if (checkRemoteIdentity && context.InDiscovery is false && c.InDiscovery is false &&
                        c.RemoteSynchronizationSourceIdentifier == context.RemoteSynchronizationSourceIdentifier)
                    {
                        if (c.RemoteSynchronizationSourceIdentifier == 0)
                        {
                            context.RemoteSynchronizationSourceIdentifier = RFC3550.Random32();
                            continue;
                        }

                        foreach (var pt in context.MediaDescription.PayloadTypes)
                        {
                            if (System.Linq.Enumerable.Contains(c.MediaDescription.PayloadTypes, pt))
                            {                                
                                Media.Common.TaggedExceptionExtensions.RaiseTaggedException(c, "Requested Remote SSRC is already in use by the context in the Tag");
                            }
                        }
                    }
                }


            //Add the context (This can introduce incorrect logic if the caller adds the context with channels in a reverse order, e.g. 2-3, 0-1)
            TransportContexts.Add(context);

            //Should check if sending is allowed via the media description
            if (context.IsActive) SendReports(context);
        }

        public /*virtual*/ bool TryAddContext(TransportContext context) { try { AddContext(context); } catch { return false; } return true; }

        /// <summary>
        /// Removes the given <see cref="TransportContext"/>
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public /*virtual*/ bool TryRemoveContext(TransportContext context)
        {
            try
            {
                return TransportContexts.Remove(context);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gets any <see cref="TransportContext"/> used by this instance.
        /// </summary>
        /// <returns>The <see cref="TransportContexts"/> used by this instance.</returns>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public /*virtual*/ System.Collections.Generic.IEnumerable<TransportContext> GetTransportContexts()
        {
            //if (IsDisposed) return Enumerable.Empty<TransportContext>();
            try
            {
                return TransportContexts;
            }
            catch (System.InvalidOperationException)
            {
                //May duplicate objects already projected, store index or use for construct.
                return GetTransportContexts();
            }
        }

        #region Rtcp


        /// <summary>
        /// Creates any <see cref="RtcpReport"/>'s which are required by the implementation.
        /// The <see cref="SendersReport"/> and <see cref="ReceiversReport"/> (And accompanying <see cref="SourceDescriptionReport"/> if bandwidth allows) are created for the given context.
        /// </summary>
        /// <param name="context">The context to prepare Rtcp reports for</param>
        /// <param name="checkBandwidth">Indicates if the bandwidth of the RtpCliet or Context given should be checked.</param>
        /// <param name="storeReports">Indicates if the reports created should be stored on the corresponding properties of the instace.</param>
        /// <returns>The RtcpReport created.</returns>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public System.Collections.Generic.IEnumerable<Rtcp.RtcpReport> PrepareReports(TransportContext context, bool checkBandwidth = true, bool storeReports = true)
        {
            //Start with a sequence of empty packets
            System.Collections.Generic.IEnumerable<Rtcp.RtcpReport> compound = System.Linq.Enumerable.Empty<Rtcp.RtcpReport>();

            int reports = 0;

            //If Rtp data was sent then send a Senders Report.
            if (context.RtpPacketsSent > 0)
            {
                //Insert the last SendersReport as the first compound packet
                compound = storeReports
                    ? System.Linq.Enumerable.Concat<Rtcp.RtcpReport>(Media.Common.Extensions.Linq.LinqExtensions.Yield((context.SendersReport = TransportContext.CreateSendersReport(context, false))), compound)
                    : System.Linq.Enumerable.Concat<Rtcp.RtcpReport>(Media.Common.Extensions.Linq.LinqExtensions.Yield(TransportContext.CreateSendersReport(context, false)), compound);

                ++reports;
            }

            //If Rtp data was received OR Rtcp data was sent then send a Receivers Report.
            if (context.RtpPacketsReceived > 0 || context.TotalRtcpBytesSent > 0)
            {
                //Insert the last ReceiversReport as the first compound packet
                compound = storeReports
                    ? System.Linq.Enumerable.Concat<Rtcp.RtcpReport>(Media.Common.Extensions.Linq.LinqExtensions.Yield((context.ReceiversReport = TransportContext.CreateReceiversReport(context, false))), compound)
                    : System.Linq.Enumerable.Concat<Rtcp.RtcpReport>(Media.Common.Extensions.Linq.LinqExtensions.Yield(TransportContext.CreateReceiversReport(context, false)), compound);

                ++reports;
            }

            //If there are any packets to be sent AND we don't care about bandwidth OR the bandwidth is not exceeded
            if (reports > 0 &&
                (checkBandwidth is false || false == context.RtcpBandwidthExceeded))
            {
                //Todo, possibly send additional items only when AverageRtcpBandwidth is not exceeded...

                //Include the SourceDescription
                compound = storeReports
                    ? System.Linq.Enumerable.Concat<Rtcp.RtcpReport>(compound, Media.Common.Extensions.Linq.LinqExtensions.Yield((context.SourceDescription = TransportContext.CreateSourceDescription(context, (string.IsNullOrWhiteSpace(ClientName) ? null : new Rtcp.SourceDescriptionReport.SourceDescriptionItem(Media.Rtcp.SourceDescriptionReport.SourceDescriptionItem.SourceDescriptionItemType.CName, System.Text.Encoding.UTF8.GetBytes(ClientName))), AdditionalSourceDescriptionItems))))
                    : System.Linq.Enumerable.Concat<Rtcp.RtcpReport>(Media.Common.Extensions.Linq.LinqExtensions.Yield(TransportContext.CreateSourceDescription(context, (string.IsNullOrWhiteSpace(ClientName) ? null : new Rtcp.SourceDescriptionReport.SourceDescriptionItem(Media.Rtcp.SourceDescriptionReport.SourceDescriptionItem.SourceDescriptionItemType.CName, System.Text.Encoding.UTF8.GetBytes(ClientName))), AdditionalSourceDescriptionItems)), compound);
            }

            //Could also put a Goodbye for inactivity ... :) Currently handled by SendGoodbye, possibly allow for optional parameter where this occurs here.

            return compound;
        }

        /// <summary>
        /// Sends any reports required for all owned TransportContexts using <see cref="SendReports"/>
        /// </summary>
        /// <returns>A value indicating if reports were immediately sent</returns>        
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public /*virtual*/ bool SendReports()
        {
            if (m_StopRequested) return false;

            bool sentAny = false;

            foreach (TransportContext tc in TransportContexts)
            {
                if (Common.IDisposedExtensions.IsNullOrDisposed(tc) is false &&
                    tc.IsRtcpEnabled && SendReports(tc))
                {
                    sentAny = true;
                }
            }

            return sentAny;
        }

        /// <summary>
        /// Sends a Goodbye to for all contained TransportContext, which will also stop the process sending or receiving after the Goodbye is sent
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public /*virtual*/ void SendGoodbyes()
        {
            foreach (RtpClient.TransportContext tc in TransportContexts)
                SendGoodbye(tc, null, tc.SynchronizationSourceIdentifier);
        }

        /// <summary>
        /// Sends a GoodbyeReport and stores it in the <paramref name="context"/> given if the <paramref name="ssrc"/> is also given and is equal to the <paramref name="context.SynchronizationSourceIdentifier"/>
        /// </summary>
        /// <param name="context">The context of the report</param>
        /// <param name="reasonForLeaving">An optional reason why the report is being sent.</param>
        /// <param name="ssrc">The optional identity to use in the report.</param>
        /// <param name="force">Indicates if the call should be forced. <see cref="IsRtcpEnabled"/>, when true the report will also not be stored</param>
        /// <returns></returns>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        protected internal /*virtual*/ int SendGoodbye(TransportContext context, byte[] reasonForLeaving = null, int? ssrc = null, bool force = false, RFC3550.SourceList sourceList = null, bool empty = false)
        {
            //Check if the Goodbye can be sent.
            if (IsUndisposed is false //If the RtpClient is disposed 
                || //OR the context is disposed
                Common.IDisposedExtensions.IsNullOrDisposed(context)
                || //OR the call has not been forced AND the context IsRtcpEnabled AND the context is active
                (force is false && context.IsRtcpEnabled && context.IsActive
                && //AND the final Goodbye was sent already
                context.Goodbye?.Transferred.HasValue is true))
            {
                //Indicate nothing was sent
                return 0;
            }

            //Make a Goodbye, indicate version in Client, allow reason for leaving and optionall other sources
            Rtcp.GoodbyeReport goodBye = TransportContext.CreateGoodbye(context, reasonForLeaving, ssrc ?? context.SynchronizationSourceIdentifier, sourceList);

            //If the sourceList is null and empty is true then indicate so by using 0 (the source should ignore, this is to indicate various things if required)
            //Context should have an option SendEmptyGoodbyeOnInactivity

            if (Common.IDisposedExtensions.IsNullOrDisposed(sourceList) && empty) goodBye.BlockCount = 0;

            //Store the Goodbye in the context if not forced the ssrc was given and it was for the context given.
            if (force is false && ssrc.HasValue && ssrc.Value.Equals(context.SynchronizationSourceIdentifier)) context.Goodbye = goodBye;

            //Send the packet and return the amount of bytes which resulted.
            return SendRtcpPackets(System.Linq.Enumerable.Concat(PrepareReports(context, false, true), Media.Common.Extensions.Linq.LinqExtensions.Yield(goodBye)));
        }

        /// <summary>
        /// Sends a <see cref="Rtcp.SendersReport"/> for each TranportChannel if allowed by the <see cref="MaximumRtcpBandwidth"/>
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public /*virtual*/ void SendSendersReports()
        {
            if (IsUndisposed is false && m_StopRequested is false)
            {
                for (int i = 0; i < TransportContexts.Count; ++i)
                {
                    TransportContext tc = TransportContexts[i];
                    SendSendersReport(tc);
                }
            }
        }

        /// <summary>
        /// Send any <see cref="SendersReport"/>'s required by the given context immediately reguardless of <see cref="MaximumRtcpBandwidth"/>
        /// Return the amount of bytes sent when sending the reports.
        /// </summary>
        /// <param name="context">The context</param>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining | System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
        protected internal /*virtual*/ int SendSendersReport(TransportContext context, bool force = false)
        {
            //Determine if the SendersReport can be sent.
            if (Common.IDisposedExtensions.IsNullOrDisposed(context) //If the context is disposed
                && //AND the call has not been forced AND the context IsRtcpEnabled 
                (false == force && true == context.IsRtcpEnabled)
                // OR there is no RtcpSocket
                || context.RtcpSocket is null)
            {
                //Indicate nothing was sent
                return 0;
            }

            //Ensure the SynchronizationSourceIdentifier of the transportChannel is assigned
            context.AssignIdentity();

            //First report include no blocks (No last senders report), store the report being sent
            context.SendersReport = TransportContext.CreateSendersReport(context, false);

            //Always send compound with SourceDescription for now
            return SendRtcpPackets(System.Linq.Enumerable.Concat(Media.Common.Extensions.Linq.LinqExtensions.Yield<Rtcp.RtcpPacket>(context.SendersReport), Media.Common.Extensions.Linq.LinqExtensions.Yield((context.SourceDescription = TransportContext.CreateSourceDescription(context)))));
        }

        /// <summary>
        /// Send any <see cref="ReceiversReports"/> required by this RtpClient instance.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public /*virtual*/ void SendReceiversReports()
        {
            if (Common.IDisposedExtensions.IsNullOrDisposed(this) || m_StopRequested) return;
            TransportContext tc;
            for (int i = 0; i < TransportContexts.Count; ++i)
            {
                tc = TransportContexts[i];
                SendReceiversReport(tc);
            }
            tc = null;
        }

        /// <summary>
        /// Send any <see cref="ReceiversReports"/>'s required by the given context immediately reguardless <see cref="MaximumRtcpBandwidth"/>
        /// Return the amount of bytes sent when sending the reports.
        /// </summary>
        /// <param name="context">The context</param>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        protected internal /*virtual*/ int SendReceiversReport(TransportContext context, bool force = false)
        {
            //Determine if the ReceiversReport can be sent.
            if (Common.IDisposedExtensions.IsNullOrDisposed(this) ||
                Common.IDisposedExtensions.IsNullOrDisposed(context)  //If the context is disposed
                && //AND the call has not been forced AND the context IsRtcpEnabled 
                (force is false && context.IsRtcpEnabled)
                // OR there is no RtcpSocket
                || context.RtcpSocket is null)
            {
                //Indicate nothing was sent
                return 0;
            }

            //Ensure the SynchronizationSourceIdentifier of the transportContext is assigned
            context.AssignIdentity();

            //create and store the receivers report sent
            context.ReceiversReport = TransportContext.CreateReceiversReport(context, false);

            //If the bandwidth is not exceeded also send a SourceDescription
            if (AverageRtcpBandwidthExceeded is false)
            {
                return SendRtcpPackets(System.Linq.Enumerable.Concat(Media.Common.Extensions.Linq.LinqExtensions.Yield<Rtcp.RtcpPacket>(context.ReceiversReport),
                    Media.Common.Extensions.Linq.LinqExtensions.Yield((context.SourceDescription = TransportContext.CreateSourceDescription(context)))));
            }

            //Just send the ReceiversReport
            return SendRtcpPackets(Media.Common.Extensions.Linq.LinqExtensions.Yield(context.ReceiversReport));
        }

        /// <summary>
        /// Selects a TransportContext by matching the SynchronizationSourceIdentifier to the given sourceid
        /// </summary>
        /// <param name="sourceId"></param>
        /// <returns>The context which was identified or null if no context was found.</returns>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        protected internal /*virtual*/ TransportContext GetContextBySourceId(int sourceId)
        {
            if (Common.IDisposedExtensions.IsNullOrDisposed(this))
                return null;

            for (int i = TransportContexts.Count - 1; i >= 0; --i)
            {
                RtpClient.TransportContext c = TransportContexts[i];

                if (Common.IDisposedExtensions.IsNullOrDisposed(c) is false &&
                    (c.SynchronizationSourceIdentifier == sourceId ||
                     c.RemoteSynchronizationSourceIdentifier == sourceId))
                    return c;
            }

            return null;
        }

        //DataChannel ControlChannel or overload?

        ////internal protected virtual TransportContext GetContextByChannel(byte channel)
        ////{
        ////    if (IsDisposed) return null;
        ////    try
        ////    {
        ////        foreach (RtpClient.TransportContext tc in TransportContexts)
        ////            if (tc.DataChannel == channel || tc.ControlChannel == channel) return tc;
        ////    }
        ////    catch (InvalidOperationException) { return GetContextByChannel(channel); }
        ////    catch { if (IsDisposed is false) throw; }
        ////    return null;
        ////}

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        protected internal /*virtual*/ TransportContext GetContextByChannels(params byte[] channels)
        {
            if (Common.IDisposedExtensions.IsNullOrDisposed(this)) return null;

            RtpClient.TransportContext c = null;

            for (int i = TransportContexts.Count - 1; i >= 0; --i)
            {
                c = TransportContexts[i];

                if (Common.IDisposedExtensions.IsNullOrDisposed(c) is false &&
                    System.MemoryExtensions.IndexOfAny(channels, c.DataChannel, c.ControlChannel) >= 0) break;

                c = null;
            }

            return c;
        }

        /// <summary>
        /// Selects a TransportContext by using the packet's Channel property
        /// </summary>
        /// <param name="packet"></param>
        /// <returns></returns>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public /*virtual*/ TransportContext GetContextForPacket(Rtcp.RtcpPacket packet)
        {
            if (Common.IDisposedExtensions.IsNullOrDisposed(this) || Common.IDisposedExtensions.IsNullOrDisposed(packet)) return null;
            //Determine based on reading the packet this is where a RtcpReport class would be useful to allow reading the Ssrc without knownin the details about the type of report
            try { return GetContextBySourceId(packet.SynchronizationSourceIdentifier); }
            catch (System.InvalidOperationException) { return GetContextForPacket(packet); }
            catch { if (Common.IDisposedExtensions.IsNullOrDisposed(this) is false) throw; }
            return null;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public /*virtual*/ void EnquePacket(Rtcp.RtcpPacket packet)
        {
            if (Common.IDisposedExtensions.IsNullOrDisposed(this) ||
                m_StopRequested ||
                Common.IDisposedExtensions.IsNullOrDisposed(packet) ||
                MaximumOutgoingPackets > 0 &&
                m_OutgoingRtpPackets.Count > MaximumOutgoingPackets)
            {
                //Turn threading on.
                ThreadEvents = true;

                //Enqueue the packet as not to drop it
                m_OutgoingRtcpPackets.Add(packet);

                return;
            }

            //Enqueue the packet
            m_OutgoingRtcpPackets.Add(packet);
        }


        /// <summary>
        /// Sends the given packets, this function assumes all packets sent belong to the same party.
        /// </summary>
        /// <param name="packets"></param>
        /// <returns></returns>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public /*virtual*/ int SendRtcpPackets(System.Collections.Generic.IEnumerable<Rtcp.RtcpPacket> packets, TransportContext context, out System.Net.Sockets.SocketError error)
        {
            error = System.Net.Sockets.SocketError.SocketError;

            if (Common.IDisposedExtensions.IsNullOrDisposed(this) || packets is null) return 0;

            //If we don't have an transportContext to send on or the transportContext has not been identified or Rtcp is Disabled or there is no remote rtcp end point
            if (Common.IDisposedExtensions.IsNullOrDisposed(context) ||
                context.SynchronizationSourceIdentifier is Common.Binary.Zero ||
                context.IsRtcpEnabled is false ||
                context.RemoteRtcp is null)
            {
                //Return
                return 0;
            }

            //Todo Determine from Context to use control channel and length. (Check MediaDescription)

            //When sending more then one packet compound packets must be padded correctly.

            //Use ToCompoundBytes to ensure that all compound packets are correctly formed.
            //Don't Just `stack` the packets as indicated if sending, assuming they are valid.

            //how manu bytes sent so far.
            int sent = 0;

            int length = 0;

            if (m_IListSockets)
            {
                System.Collections.Generic.List<System.ArraySegment<byte>> buffers = [];

                //Try to get the buffer for each packet
                foreach (Rtcp.RtcpPacket packet in packets)
                {
                    //If we can
                    if (packet.TryGetBuffers(out System.Collections.Generic.IList<System.ArraySegment<byte>> packetBuffers))
                    {
                        //Add those buffers
                        buffers.AddRange(packetBuffers);

                        //Keep track of the length
                        length += packet.Length;
                    }
                    else
                    {
                        //Just send them in their own array.
                        sent += SendData(System.Linq.Enumerable.ToArray(RFC3550.ToCompoundBytes(packets)),
                            context.ControlChannel, context.RtcpSocket, context.RemoteRtcp, out error);

                        buffers = null;

                        break;
                    }

                }

                //If nothing was sent and the buffers are not null and the socket is tcp use framing.
                if (length > 0 && context.IsActive && sent is 0 && buffers is not null)
                {
                    if (context.RtcpSocket.ProtocolType == System.Net.Sockets.ProtocolType.Tcp)
                    {
                        //Todo, should have function to create framing to be compatible with RFC4571
                        //Todo, Int can be used as bytes and there may only be 2 bytes required.
                        byte[] framing = new byte[] { BigEndianFrameControl, context.ControlChannel, 0, 0 };

                        Common.Binary.Write16(framing, 2, Common.Binary.IsLittleEndian, (short)length);

                        //Add the framing
                        buffers.Insert(0, new System.ArraySegment<byte>(framing));
                    }

                    //Send that data.
                    sent += context.RtcpSocket.Send(buffers, System.Net.Sockets.SocketFlags.None, out error);
                }
            }
            else
            {

                //Iterate the packets
                foreach (Rtcp.RtcpPacket packet in packets)
                {
                    //If the data is not contigious
                    if (packet.IsContiguous() is false)
                    {
                        //Just send all packets in their own array by projecting the data (causes an allocation)
                        sent += SendData(System.Linq.Enumerable.ToArray(RFC3550.ToCompoundBytes(packets)),
                            context.ControlChannel, context.RtcpSocket, context.RemoteRtcp, out error);

                        //Stop here.
                        break;
                    }

                    //Account for the length of the packet
                    length += packet.Length;
                }

                //If nothing was sent then send the data now.
                if (length > 0 && sent is 0)
                {
                    //Send the framing seperately to keep the allocations minimal.

                    //Note, Live555 and LibAV may not be able to handle this, use IListSockets to work around.
                    if (context.RtcpSocket.ProtocolType == System.Net.Sockets.ProtocolType.Tcp)
                    {
                        //Todo, should have function to create framing to be compatible with RFC4571
                        //Todo, Int can be used as bytes and there may only be 2 bytes required.
                        byte[] framing = new byte[] { BigEndianFrameControl, context.ControlChannel, 0, 0 };

                        Common.Binary.Write16(framing, 2, Common.Binary.IsLittleEndian, (short)length);

                        while (sent < InterleavedOverhead &&
                            (false.Equals(error == System.Net.Sockets.SocketError.ConnectionAborted) &&
                            false.Equals(error == System.Net.Sockets.SocketError.ConnectionReset) &&
                            false.Equals(error == System.Net.Sockets.SocketError.NotConnected)))
                        {
                            //Send all the framing.
                            sent += context.RtcpSocket.Send(framing, sent, InterleavedOverhead - sent, System.Net.Sockets.SocketFlags.None, out error);
                        }

                        sent = 0;
                    }
                    else error = System.Net.Sockets.SocketError.Success;

                    int packetLength;

                    //if the framing was delivered then send the packet
                    if (error == System.Net.Sockets.SocketError.Success) foreach (Rtcp.RtcpPacket packet in packets)
                        {
                            //cache the length
                            packetLength = packet.Length;

                            //While there is data to send
                            while (sent < packetLength &&
                                false.Equals(error == System.Net.Sockets.SocketError.ConnectionAborted) &&
                                false.Equals(error == System.Net.Sockets.SocketError.ConnectionReset))
                            {
                                //Send it.
                                sent += context.RtcpSocket.Send(packet.Header.First16Bits.m_Memory.Array,
                                    packet.Header.First16Bits.m_Memory.Offset + sent, packetLength - sent,
                                    System.Net.Sockets.SocketFlags.None, out error);
                            }

                            //Reset offset.
                            sent = 0;
                        }

                    //Set sent to how many bytes were sent.
                    sent = length + InterleavedOverhead;
                }
            }

            //should be doing this when each was sent or not at all..

            //If the compound bytes were completely sent then all packets have been sent
            if (error == System.Net.Sockets.SocketError.Success)
            {
                //Check to see if each packet which was sent
                int csent = 0;

                //Iterate each managed packet to determine if it was completely sent.
                foreach (Rtcp.RtcpPacket packet in packets)
                {
                    //Handle null or disposed packets.
                    if (Common.IDisposedExtensions.IsNullOrDisposed(packet)) continue;

                    //Increment for the length of the packet
                    csent += packet.Length;

                    //If more data was contained then sent don't set Transferred and raise and event
                    if (csent > sent)
                    {
                        ++context.m_FailedRtcpTransmissions;

                        continue;
                    }

                    //set sent
                    packet.Transferred = System.DateTime.UtcNow;

                    //Raise en event
                    HandleOutgoingRtcpPacket(this, packet, context);
                }
            }

            return sent;
        }

        public /*virtual*/ int SendRtcpPackets(System.Collections.Generic.IEnumerable<Rtcp.RtcpPacket> packets)
        {
            if (packets is null) return 0;

            TransportContext context = GetContextForPacket(System.Linq.Enumerable.FirstOrDefault(packets));

            return SendRtcpPackets(packets, context, out _);
        }

        internal /*virtual*/ bool SendReports(TransportContext context, bool force = false)
        {

            return SendReports(context, out System.Net.Sockets.SocketError error, force);
        }

        //Todo, remove virtuals or not.

        /// <summary>
        /// Sends any <see cref="RtcpReport"/>'s immediately for the given <see cref="TransportContext"/> if <see cref="AverageRtcpBandwidthExceeded"/> is false.
        /// </summary>
        /// <param name="context">The <see cref="TransportContext"/> to send a report for</param>
        /// <param name="error"></param>
        /// <param name="force"></param>
        /// <returns>A value indicating if reports were sent</returns>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
        internal /*virtual*/ bool SendReports(TransportContext context, out System.Net.Sockets.SocketError error, bool force = false)
        {
            //Ensure set
            error = System.Net.Sockets.SocketError.SocketError;

            //Check for the stop signal (or disposal)
            if (force is false && m_StopRequested || Common.IDisposedExtensions.IsNullOrDisposed(this) ||  //Otherwise
                context.IsRtcpEnabled is false
                || //Or Rtcp Bandwidth for this context or RtpClient has been exceeded
                context.RtcpBandwidthExceeded || AverageRtcpBandwidthExceeded
                || false.Equals(context.Goodbye is null)) return false; //No reports can be sent.


            //If forced or the last reports were sent in less time than alloted by the m_SendInterval
            //Indicate if reports were sent in this interval
            return (force || context.LastRtcpReportSent == System.TimeSpan.MinValue || context.LastRtcpReportSent >= context.m_SendInterval) && SendRtcpPackets(PrepareReports(context, true, true), context, out error) > 0;
        }

        /// <summary>
        /// Sends a RtcpGoodbye Immediately if the given context:
        /// <see cref="IsRtcpEnabled"/>  and the context has not received a RtcpPacket during the last <see cref="ReceiveInterval"/>.
        /// OR
        /// <see cref="IsRtpEnabled"/> and the context <see cref="IsContinious"/> but <see cref="Uptime"/> is > the <see cref="MediaEndTime"/>
        /// </summary>
        /// <param name="lastActivity">The time the lastActivity has occured on the context (sending or recieving)</param>
        /// <param name="context">The context to check against</param>
        /// <returns>True if the connection is inactive and a Goodebye was attempted to be sent to the remote party</returns>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal /*virtual*/ bool SendGoodbyeIfInactive(System.DateTime lastActivity, TransportContext context)
        {
            bool inactive = false;

            if (Common.IDisposedExtensions.IsNullOrDisposed(this)
                ||
                m_StopRequested
                ||
                RtcpEnabled is false
                ||
                context.HasRecentRtpActivity
                ||
                context.HasRecentRtcpActivity
                || //If the context has a continous flow OR the general Uptime is less then context MediaEndTime
                (context.IsContinious is false && Uptime < context.MediaEndTime))
            {
                return false;
            }

            //Calulcate for the currently inactive time period
            if (context.Goodbye is null &&
                context.HasAnyRecentActivity is false)
            {
                //Set the amount of time inactive
                context.m_InactiveTime = System.DateTime.UtcNow - lastActivity;

                //Determine if the context is not inactive too long
                //6.3.5 Timing Out an SSRC
                //I use the recieve interval + the send interval
                //It should be standarly 2 * recieve interval
                if (context.m_InactiveTime >= context.m_ReceiveInterval + context.m_SendInterval)
                {
                    //send a goodbye
                    SendGoodbye(context, null, context.SynchronizationSourceIdentifier);

                    //mark inactive
                    inactive = true;

                    //Disable further service
                    //context.IsRtpEnabled = context.IsRtcpEnabled = false;
                }
                else if (context.m_InactiveTime >= context.m_ReceiveInterval + context.m_SendInterval)
                {
                    //send a goodbye but don't store it
                    inactive = SendGoodbye(context) <= 0;
                }
            }

            //indicate a goodbye was sent and a context is now inactive.
            return inactive;
        }

        #endregion

        #region Rtp

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public TransportContext GetContextForMediaDescription(Sdp.MediaDescription mediaDescription)
        {
            if (Common.IDisposedExtensions.IsNullOrDisposed(mediaDescription)) return null;

            RtpClient.TransportContext c = null;

            for (int i = TransportContexts.Count - 1; i >= 0; --i)
            {
                c = TransportContexts[i];

                if (c.MediaDescription.MediaType == mediaDescription.MediaType &&
                    c.MediaDescription.MediaFormat.Equals(mediaDescription.MediaFormat, System.StringComparison.InvariantCultureIgnoreCase)
                    ||
                    c.MediaDescription.ControlLine is not null &&
                    c.MediaDescription.ControlLine.Equals(mediaDescription.ControlLine)) break;

                c = null;
            }

            return c;

        }

        /// <summary>
        /// Selects a TransportContext for a RtpPacket by matching the packet's PayloadType to the TransportContext's MediaDescription.MediaFormat
        /// </summary>
        /// <param name="packet"></param>
        /// <returns></returns>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public /*virtual */ TransportContext GetContextForPacket(RtpPacket packet)
        {
            return Common.IDisposedExtensions.IsNullOrDisposed(packet)
                ? null
                : GetContextBySourceId(packet.SynchronizationSourceIdentifier) ?? GetContextByPayloadType(packet.PayloadType);

            //COuld improve by checking both at the same time
            //return TransportContexts.FirstOrDefault( c=> false == IDisposedExtensions.IsNullOrDisposed(c) && c.SynchronizationSourceIdentifier == 
        }

        /// <summary>
        /// Selects a TransportContext for a RtpPacket by matching the packet's PayloadType to the TransportContext's MediaDescription.MediaFormat
        /// </summary>
        /// <param name="packet"></param>
        /// <returns></returns>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public /*virtual */ TransportContext GetContextForFrame(RtpFrame frame)
        {
            return Common.IDisposedExtensions.IsNullOrDisposed(frame)
                ? null
                : TransportContexts.Count is 0 ? null : GetContextBySourceId(frame.SynchronizationSourceIdentifier) ?? GetContextByPayloadType(frame.PayloadType);
        }

        /// <summary>
        /// Selects a TransportContext by matching the given payloadType to the TransportContext's MediaDescription.MediaFormat
        /// </summary>
        /// <param name="payloadType"></param>
        /// <returns></returns>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public /*virtual */ TransportContext GetContextByPayloadType(int payloadType)
        {
            RtpClient.TransportContext c = null;

            for (int i = TransportContexts.Count - 1; i >= 0; --i)
            {
                c = TransportContexts[i];

                if (Common.IDisposedExtensions.IsNullOrDisposed(c) is false &&
                    false.Equals(Common.IDisposedExtensions.IsNullOrDisposed(c.MediaDescription)) &&
                    System.Linq.Enumerable.Contains(c.MediaDescription.PayloadTypes, payloadType)) break;

                c = null;
            }

            return c;
        }

        /// <summary>
        /// Selects a TransportContext by matching the given socket handle to the TransportContext socket's handle
        /// </summary>
        /// <param name="payloadType"></param>
        /// <returns></returns>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public /*virtual */ TransportContext GetContextBySocketHandle(nint socketHandle)
        {
            RtpClient.TransportContext c = null;

            for (int i = TransportContexts.Count - 1; i >= 0; --i)
            {
                c = TransportContexts[i];

                if (Common.IDisposedExtensions.IsNullOrDisposed(c) is false &&
                    c.IsActive &&
                    (c.RtpSocket is not null && c.RtpSocket.Handle == socketHandle ||
                     c.RtcpSocket is not null && c.RtcpSocket.Handle == socketHandle)) break;

                c = null;
            }

            return c;
        }

        /// <summary>
        /// Selects a TransportContext by matching the given socket handle to the TransportContext socket's handle
        /// </summary>
        /// <param name="payloadType"></param>
        /// <returns></returns>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public /*virtual */ TransportContext GetContextBySocket(System.Net.Sockets.Socket socket)
        {
            return socket is null ? null : GetContextBySocketHandle(socket.Handle);
        }

        /// <summary>
        /// Adds a packet to the queue of outgoing RtpPackets
        /// </summary>
        /// <param name="packet">The packet to enqueue</param> (used to take the RtpCLient too but we can just check the packet payload type
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public /*virtual */ void EnquePacket(RtpPacket packet)
        {
            if (Common.IDisposedExtensions.IsNullOrDisposed(this) || m_StopRequested || Common.IDisposedExtensions.IsNullOrDisposed(packet)) return;

            //Add a the packet to the outgoing
            m_OutgoingRtpPackets.Add(packet);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public /*virtual */ void EnqueFrame(RtpFrame frame)
        {
            if (Common.IDisposedExtensions.IsNullOrDisposed(this) || m_StopRequested || Common.IDisposedExtensions.IsNullOrDisposed(frame)) return;

            for (int i = 0, e = frame.Count; i < e; ++i)
            {
                EnquePacket(frame[i]);
            }

            foreach (RtpPacket packet in frame) EnquePacket(packet);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
        public void SendRtpFrame(RtpFrame frame, out System.Net.Sockets.SocketError error, int? ssrc = null)
        {
            error = System.Net.Sockets.SocketError.SocketError;

            if (m_StopRequested || Common.IDisposedExtensions.IsNullOrDisposed(frame)) return;

            TransportContext transportContext = ssrc.HasValue ? GetContextBySourceId(ssrc.Value) : GetContextByPayloadType(frame.PayloadType);

            RtpPacket p;

            for (int i = 0, e = frame.Count; i < e; ++i)
            {
                p = frame[i];

                SendRtpPacket(p, transportContext, out error, ssrc);

                if (false.Equals(error == System.Net.Sockets.SocketError.Success)) break;
            }

            //p = null;
        }

        public void SendRtpFrame(RtpFrame frame, int? ssrc = null)
        {

            SendRtpFrame(frame, out System.Net.Sockets.SocketError error, ssrc);
        }

        /// <summary>
        /// Sends a RtpPacket to the connected client.
        /// </summary>
        /// <param name="packet">The RtpPacket to send</param>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
        public int SendRtpPacket(RtpPacket packet, TransportContext transportContext, out System.Net.Sockets.SocketError error, int? ssrc = null) //Should be compatible with the Prepare signature.
        {
            error = System.Net.Sockets.SocketError.SocketError;

            if (m_StopRequested || Common.IDisposedExtensions.IsNullOrDisposed(packet)) return 0;

            //Context could already be known, ssrc may have value.

            //TransportContext transportContext = ssrc.HasValue ? GetContextBySourceId(ssrc.Value) : GetContextForPacket(packet);

            transportContext ??= ssrc.HasValue ? GetContextBySourceId(ssrc.Value) : GetContextForPacket(packet);

            //If we don't have an transportContext to send on or the transportContext has not been identified
            if (Common.IDisposedExtensions.IsNullOrDisposed(transportContext) || transportContext.IsActive is false) return 0;

            //Ensure not sending too large of a packet
            if (packet.Length > transportContext.MaximumPacketSize) Media.Common.TaggedExceptionExtensions.RaiseTaggedException(transportContext, "See Tag. The given packet must be smaller than the value in the transportContext.MaximumPacketSize.");

            //How many bytes were sent
            int sent = 0;

            int length = packet.Length;

            #region Unused [Sends a SendersReport if one was not already]

            //Send a SendersReport before any data is sent.
            //if (transportContext.SendersReport is null && transportContext.IsRtcpEnabled) SendSendersReport(transportContext);

            #endregion

            //Keep track if we have to dispose the packet.
            bool dispose = false;

            if (m_IListSockets)
            {

                if (ssrc.HasValue && false.Equals(ssrc.Value.Equals(packet.SynchronizationSourceIdentifier)))
                {
                    //Temporarily make a new packet with the same data and new header with the correct ssrc.
                    packet = new RtpPacket(new RtpHeader(packet.Version, packet.Padding, packet.Extension, packet.Marker, packet.PayloadType, packet.ContributingSourceCount, ssrc.Value, packet.SequenceNumber, packet.Timestamp), new Common.MemorySegment(packet.Payload));

                    //mark to dispose the packet instance
                    dispose = true;
                }

                //If we can get the buffer from the packet
                if (packet.TryGetBuffers(out System.Collections.Generic.IList<System.ArraySegment<byte>> buffers))
                {
                    //If Tcp
                    if ((int)transportContext.RtpSocket.ProtocolType == (int)System.Net.Sockets.ProtocolType.Tcp)
                    {
                        //Todo, Int can be used as bytes and there may only be 2 bytes required.
                        byte[] framing = new byte[] { BigEndianFrameControl, transportContext.DataChannel, 0, 0 };

                        //Write the length
                        Common.Binary.Write16(framing, 2, Common.Binary.IsLittleEndian, (short)length);

                        //Add the framing
                        buffers.Insert(0, new System.ArraySegment<byte>(framing));
                    }

                    //Send that data.
                    sent += transportContext.RtpSocket.Send(buffers, System.Net.Sockets.SocketFlags.None, out error);
                }
                else
                {
                    //If the transportContext is changed to automatically update the timestamp by frequency then use transportContext.RtpTimestamp
                    sent += SendData(System.Linq.Enumerable.ToArray(packet.Prepare(null, ssrc, null, null)), transportContext.DataChannel, transportContext.RtpSocket, transportContext.RemoteRtp, out error, (int)transportContext.m_SendInterval.TotalMicroseconds >> 2);
                }
            }
            else
            {
                //If the ssrc does not have value and the packet is contigious then it can be sent in place.

                //Check if the packet cannot be sent in place
                if (ssrc.HasValue && false.Equals(ssrc.Equals(packet.SynchronizationSourceIdentifier))
                    ||
                    packet.IsContiguous() is false)
                {

                    //If the transportContext is changed to automatically update the timestamp by frequency then use transportContext.RtpTimestamp
                    sent += SendData(System.Linq.Enumerable.ToArray(packet.Prepare(null, ssrc, null, null)),
                        transportContext.DataChannel, transportContext.RtpSocket, transportContext.RemoteRtp, out error,
                        (int)transportContext.m_SendInterval.TotalMicroseconds >> 2);
                }
                else
                {
                    //Send the data in place.
                    sent += SendData(packet.Header.First16Bits.m_Memory.Array, packet.Header.First16Bits.m_Memory.Offset, packet.Length,
                        transportContext.DataChannel, transportContext.RtpSocket, transportContext.RemoteRtp, out error,
                        (int)transportContext.m_SendInterval.TotalMicroseconds >> 2);
                }
            }

            if (error == System.Net.Sockets.SocketError.Success && sent >= length)
            {
                packet.Transferred = System.DateTime.UtcNow;

                //Handle the packet outgoing.
                HandleOutgoingRtpPacket(this, packet, transportContext);
            }
            else
            {
                ++transportContext.m_FailedRtpTransmissions;
            }

            if (dispose) packet.Dispose();

            return sent;
        }

        //virtual?

        public int SendRtpPacket(RtpPacket packet, int? ssrc = null)
        {

            TransportContext transportContext = ssrc.HasValue ? GetContextBySourceId(ssrc.Value) : GetContextForPacket(packet);

            return SendRtpPacket(packet, transportContext, out System.Net.Sockets.SocketError error, ssrc);
        }

        public int SendRtpPacket(RtpPacket packet, TransportContext context)
        {

            return SendRtpPacket(packet, context, out System.Net.Sockets.SocketError error);
        }

        #endregion

        /// <summary>
        /// Creates and starts a worker thread which will send and receive data as required.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public /*virtual*/ void Activate()
        {
            try
            {
                //If the worker thread is already active then return
                if (m_StopRequested is false && IsActive) return;

                //Create the worker thread
                m_WorkerThread = new System.Threading.Thread(SendReceieve)
                {
                    Name = "RtpClient-" + InternalId,
                    //Start highest.
                    Priority = System.Threading.ThreadPriority.Highest
                };

                //Configure
                ConfigureThread(m_WorkerThread); //name and ILogging

                //Reset stop signal
                m_StopRequested = false;

                //Start thread
                m_WorkerThread.Start();

                //Wait for thread to actually start
                while (IsActive is false)
                    m_EventReady.Wait(Common.Extensions.TimeSpan.TimeSpanExtensions.OneTick);

                //Could also use the Join but would have to add logic in the thread to handle this.
                //m_WorkerThread.Join(Common.Extensions.TimeSpan.TimeSpanExtensions.OneTick);
            }
            catch (System.ObjectDisposedException)
            {
                return;
            }
            catch (System.Exception ex)
            {
                Common.ILoggingExtensions.LogException(Logger, ex);

                throw;
            }
        }

        /// <summary>
        /// Sends the Rtcp Goodbye and signals a stop in the worker thread.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Deactivate()
        {
            if (Common.IDisposedExtensions.IsNullOrDisposed(this) || IsActive is false) return;

            SendGoodbyes();

            m_StopRequested = true;

            foreach (TransportContext tc in TransportContexts)
                if (tc.IsActive) tc.DisconnectSockets();

            Media.Common.Extensions.Thread.ThreadExtensions.TryAbortAndFree(ref m_WorkerThread);

            Started = System.DateTime.MinValue;

            m_EventData.Clear();

            m_EventReady.Set();
        }

        public void DisposeAndClearTransportContexts()
        {
            //Dispose contexts
            foreach (TransportContext tc in TransportContexts) tc.Dispose();

            //Counters go away with the transportChannels
            TransportContexts.Clear();
        }

        /// <summary>
        /// Returns the amount of bytes read to completely read the application layer framed data
        /// Where a negitive return value indicates no more data remains.
        /// </summary>
        /// <param name="received">How much data was received</param>
        /// <param name="frameChannel">The output of reading a frameChannel</param>
        /// <param name="context">The context assoicated with the frameChannel</param>
        /// <param name="offset">The reference to offset to look for framing data</param>
        /// <param name="raisedEvent">Indicates if an event was raised</param>
        /// <param name="buffer">The optional buffer to use.</param>
        /// <returns>The amount of bytes the frame data SHOULD have</returns>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private int ReadApplicationLayerFraming(ref int received, ref int sessionRequired, ref int offset, out byte? frameChannel, out RtpClient.TransportContext context, out bool raisedEvent, byte[] buffer = null)
        {
            //There is no relevant TransportContext assoicated yet.
            context = null;

            //The channel of the frame - The Framing Method
            frameChannel = default;

            raisedEvent = false;

            buffer ??= m_Buffer.Array;

            int bufferLength = buffer.Length, bufferOffset = offset;

            received = Common.Binary.Min(received, bufferLength - bufferOffset);

            //Assume given enough for sessionRequired

            //Todo Determine from Context to use control channel and length. (Check MediaDescription)
            //NEEDS TO HANDLE CASES WHERE RFC4571 Framing are in play and no $ or Channel are used....
            //int sessionRequired = InterleavedOverhead;

            if (received <= 0 || sessionRequired < 0 || received < sessionRequired) return -1;

            //The amount of data needed for the frame comes from TryReadFrameHeader
            bool isInterleaved = sessionRequired >= InterleavedOverhead;

            //When there was something to delemit the frames then we can do a quick search for it.
            if (isInterleaved)
            {
                //Look for the frame control octet
                int startOfFrame = System.Array.IndexOf(buffer, BigEndianFrameControl, bufferOffset, received);

                //If not found everything belongs to the upper layer
                if (startOfFrame == -1)
                {
                    //System.Diagnostics.Debug.WriteLine("Interleaving: " + received);
                    OnOutOfBandData(buffer, bufferOffset, received);

                    raisedEvent = true;

                    //Indicate the amount of data consumed.
                    return received;
                }

                // If the start of the frame is not at the beginning of the buffer
                if (startOfFrame > bufferOffset)
                {
                    //Determine the amount of data which belongs to the upper layer
                    int upperLayerData = startOfFrame - bufferOffset;

                    //System.Diagnostics.Debug.WriteLine("Moved To = " + startOfFrame + " Of = " + received + " - Bytes = " + upperLayerData + " = " + Encoding.ASCII.GetString(m_Buffer, mOffset, startOfFrame - mOffset));                

                    OnOutOfBandData(buffer, bufferOffset, upperLayerData);

                    raisedEvent = true;

                    //Indicate length from offset until next possible frame.
                    //(should always be positive, if somehow -1 is returned this will
                    //signal a end of buffer to callers)

                    //If there is more data related to upperLayerData it will be evented
                    //in the next run. (See RtspClient ProcessInterleaveData notes)
                    return upperLayerData;
                }

                //If there is not enough data for a frame header return
                if (bufferOffset + sessionRequired > bufferLength)
                {
                    return -1;
                }
            }

            int frameLength = TryReadFrameHeader(buffer, bufferOffset, out frameChannel, isInterleaved ? BigEndianFrameControl : null, isInterleaved, sessionRequired);

            //Assign a context if there is a frame of any size
            if (frameChannel.HasValue && frameLength >= 0)
            {
                //Assign the context
                context = GetContextByChannels(frameChannel.Value);

                //Increase the result by the size of the header
                frameLength += sessionRequired;
            }
            else if (frameLength >= 0) //Have to determine context by inspecting packet headers...
            {
                //Increase the result by the size of the header
                frameLength += sessionRequired;

                #region Verify Packet Headers

                //Use CommonHeaderBits on the data after the Interleaved Frame Header
                using RFC3550.CommonHeaderBits commonHeaderBits = new(buffer, offset + sessionRequired);

                //Try to mark the packetas compatible and find a context
                bool incompatible = false, expectRtcp = false, expectRtp = false;

                int remainingInBuffer = received - (offset + sessionRequired);

                //Perform a set of checks and set weather or not Rtp or Rtcp was expected.                                  
                if (incompatible is false)
                {
                    //Determine if the packet is Rtcp 
                    if (remainingInBuffer <= sessionRequired + Rtcp.RtcpHeader.Length)
                    {
                        //Remove the context
                        context = null;

                        //return the frameLength read...
                        return frameLength;
                    }

                    //use a rtcp header to extract the information in the packet
                    using Rtcp.RtcpHeader rtcpHeader = new(buffer, offset + sessionRequired);

                    //Get the length in 'words' (by adding one)
                    //A length of 0 means 1 word
                    //A length of 65535 means only the header (no ssrc [or payload])
                    ushort lengthInWordsPlusOne = (ushort)(rtcpHeader.LengthInWordsMinusOne + 1);

                    //Store any rtcp length so we can verify its not 0 and then additionally ensure its value is not larger then the frameLength
                    //Convert to bytes
                    int rtcpLen = lengthInWordsPlusOne * 4;

                    //Check that the supposed  amount of contained words is greater than or equal to the frame length conveyed by the application layer framing
                    //it must also be larger than the buffer
                    incompatible = rtcpLen >= frameLength || rtcpLen >= bufferLength;

                    //if rtcpLen >= ushort.MaxValue the packet may possibly span multiple segments unless a large buffer is used.

                    if (incompatible is false && //It was not already ruled incomaptible
                        lengthInWordsPlusOne > 0 && //If there is supposed to be SSRC in the packet
                        rtcpHeader.Size > Rtcp.RtcpHeader.Length)
                    {
                        //Determine if Rtcp is expected
                        //Perform another lookup and check compatibility
                        expectRtcp = !(incompatible = Common.IDisposedExtensions.IsNullOrDisposed(context = GetContextBySourceId(rtcpHeader.SendersSynchronizationSourceIdentifier)));
                    }

                    //May be mixing channels...
                    if (expectRtcp is false)
                    {
                        //Rtp
                        if (remainingInBuffer <= sessionRequired + Rtp.RtpHeader.Length)
                        {
                            //Remove the context
                            context = null;

                            //return the frameLength read...
                            return frameLength;
                        }

                        //the context by payload type is null is not discovering the identity check the SSRC.
                        if (Common.IDisposedExtensions.IsNullOrDisposed(context = GetContextByPayloadType(commonHeaderBits.RtpPayloadType)) is false && context.InDiscovery is false)
                        {
                            using Rtp.RtpHeader rtpHeader = new(buffer, offset + sessionRequired);

                            expectRtp = !(incompatible = Common.IDisposedExtensions.IsNullOrDisposed(context = GetContextBySourceId(rtpHeader.SynchronizationSourceIdentifier)));
                        }
                        else incompatible = false;
                    }
                }

                #endregion
            }

            //Return the amount of bytes or -1 if any error occured.
            return frameLength;
        }

        /// <summary>
        /// Sends the given data on the socket remote
        /// </summary>
        /// <param name="data"></param>
        /// <param name="channel"></param>
        /// <param name="socket"></param>
        /// <param name="remote"></param>
        /// <param name="error"></param>
        /// <param name="useFrameControl"></param>
        /// <param name="useChannelId"></param>
        /// <returns></returns>
        protected internal /*virtual*/ int SendData(byte[] data, byte? channel, System.Net.Sockets.Socket socket, System.Net.EndPoint remote, out System.Net.Sockets.SocketError error, int pollTime = 0, bool useFrameControl = true, bool useChannelId = true)
        {
            return SendData(data, 0, data.Length, channel, socket, remote, out error, pollTime, useFrameControl, useChannelId);
        }

        /// <summary>
        /// Sends the given data on the socket to remote
        /// </summary>
        /// <param name="data"></param>
        /// <param name="offset"></param>
        /// <param name="length"></param>
        /// <param name="channel"></param>
        /// <param name="socket"></param>
        /// <param name="remote"></param>
        /// <param name="error"></param>
        /// <param name="useFrameControl"></param>
        /// <param name="useChannelId"></param>
        /// <returns></returns>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        protected internal /*virtual*/ int SendData(byte[] data, int offset, int length, byte? channel, System.Net.Sockets.Socket socket, System.Net.EndPoint remote, out System.Net.Sockets.SocketError error, int pollTime = 0, bool useFrameControl = true, bool useChannelId = true)
        {
            error = System.Net.Sockets.SocketError.SocketError;

            //Check there is valid data and a socket which is able to write and that the RtpClient is not stopping
            if (Common.IDisposedExtensions.IsNullOrDisposed(this) || socket is null || length is 0 || data is null) return 0;

            int sent = 0;

            //Todo could pass TransportContext also....

            //Just had the context in the previous call in most cases...
            //RtpClient.TransportContext tc = context ?? GetContextBySocket(socket);

            try
            {
                #region Tcp Application Layer Framing

                //Under Tcp we must frame the data for the given channel
                if (socket.ProtocolType == System.Net.Sockets.ProtocolType.Tcp && channel.HasValue)
                {
                    //Create the data from the concatenation of the frame header and the data existing
                    //E.g. Under RTSP...Frame the Data in a PDU {$ C LEN ...}

                    //Could set SendBufferSize now.

                    //int sbs = socket.SendBufferSize;

                    //socket.SendBufferSize = length;

                    if (useChannelId && useFrameControl)
                    {
                        //Data now has an offset and length...
                        //data = Enumerable.Concat(Media.Common.Extensions.Linq.LinqExtensions.Yield(BigEndianFrameControl), Media.Common.Extensions.Linq.LinqExtensions.Yield(channel.Value))
                        //    .Concat(Binary.GetBytes((short)length, Common.Binary.IsLittleEndian))
                        //    .Concat(data).ToArray();

                        //Create the framing
                        byte[] framing = new byte[] { BigEndianFrameControl, channel.Value, 0, 0 };

                        //Write the length
                        Common.Binary.Write16(framing, 2, Common.Binary.IsLittleEndian, (short)length);

                        //See if we can write.
                        if (false == socket.Poll(pollTime, System.Net.Sockets.SelectMode.SelectWrite))
                        {
                            //Indicate the operation has timed out
                            error = System.Net.Sockets.SocketError.TimedOut;

                            return sent;
                        }

                        //Send the framing
                        sent += Common.Extensions.Socket.SocketExtensions.SendTo(framing, 0, InterleavedOverhead, socket, remote, System.Net.Sockets.SocketFlags.None, out error);

                        //After small writes do a read. (make sure we don't get back our own data in collision)

                    }
                    else
                    {
                        //Build the data
                        System.Collections.Generic.IEnumerable<byte> framingData;

                        //The length is always present
                        framingData = System.Linq.Enumerable.Concat(Common.Binary.GetBytes((short)length, Common.Binary.IsLittleEndian), data);

                        int framingLength = 2;

                        if (useChannelId)
                        {
                            framingData = System.Linq.Enumerable.Concat(Media.Common.Extensions.Linq.LinqExtensions.Yield(channel.Value), framingData);
                            ++framingLength;
                        }

                        if (useFrameControl)
                        {
                            //data = Media.Common.Extensions.Linq.LinqExtensions.Yield(BigEndianFrameControl).Concat(data).ToArray();
                            framingData = System.Linq.Enumerable.Concat(Media.Common.Extensions.Linq.LinqExtensions.Yield(BigEndianFrameControl), data);
                            ++framingLength;
                        }

                        //Project the framing.
                        byte[] framing = System.Linq.Enumerable.ToArray(framingData);

                        //See if we can write.
                        if (false == socket.Poll(pollTime, System.Net.Sockets.SelectMode.SelectWrite))
                        {
                            //Indicate the operation has timed out
                            error = System.Net.Sockets.SocketError.TimedOut;

                            return sent;
                        }

                        sent += Common.Extensions.Socket.SocketExtensions.SendTo(framing, 0, framingLength, socket, remote, System.Net.Sockets.SocketFlags.None, out error);

                    }

                    //Put back
                    //socket.SendBufferSize = sbs;

                    //Must send framing seperately.
                    //MSS cannot be determined easily without hacks or custom socket layer.
                    //Framing was not included in the bytesPerPacket when packetization was performed.
                    //If framing is missed or dropped the reciever implementation should be using a packet inspection routine similar to the one implemented in this library to demux the packet.
                    //This has reprocussions if this client is a proxy as two different ssrc's may overlap and only have different control channels....

                }
                else length = data.Length;

                #endregion

                //Check for the socket to be writable in the receive interval of the context
                if (false == socket.Poll(pollTime, System.Net.Sockets.SelectMode.SelectWrite))
                {
                    //Indicate the operation has timed out
                    error = System.Net.Sockets.SocketError.TimedOut;

                    return sent;
                }

                //Send all the data to the endpoint
                sent += Common.Extensions.Socket.SocketExtensions.SendTo(data, offset, length, socket, remote, System.Net.Sockets.SocketFlags.None, out error);

                return sent; //- Overhead for tcp, may not have to include it.
            }
            catch
            {
                //Something bad happened, usually disposed already
                return sent;
            }
        }

        /// <summary>
        /// Recieves data on a given socket and endpoint
        /// </summary>
        /// <param name="socket">The socket to receive data on</param>
        /// <returns>The number of bytes received</returns>             
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        protected internal /*virtual*/ int ReceiveData(System.Net.Sockets.Socket socket, ref System.Net.EndPoint remote, out System.Net.Sockets.SocketError error, bool expectRtp = true, bool expectRtcp = true, Common.MemorySegment buffer = null)
        {
            //Nothing bad happened yet.
            error = System.Net.Sockets.SocketError.SocketError;

            if (Common.IDisposedExtensions.IsNullOrDisposed(buffer)) buffer = m_Buffer;

            //Ensure the socket can poll, should measure against parallel checks with OR
            if (buffer.Count <= 0 ||
                m_StopRequested ||
                socket is null ||
                remote is null ||
                Common.IDisposedExtensions.IsNullOrDisposed(buffer) ||
                Common.IDisposedExtensions.IsNullOrDisposed(this)) return 0;

            bool tcp = socket.ProtocolType == System.Net.Sockets.ProtocolType.Tcp;

            //Cache the offset at the time of the call
            int received = 0, justRecieved;

            //int max = buffer.Count;

            //int pmax;

            //if (tcp) Media.Common.Extensions.Socket.SocketExtensions.GetMaximumSegmentSize(socket, out pmax);
            //else pmax = Media.Common.Extensions.Socket.SocketExtensions.GetMaximumTransmittableUnit(socket);

            try
            {
                //Determine how much data is 'Available'
                //int available = socket.ReceiveFrom(m_Buffer.Array, offset, m_Buffer.Count, SocketFlags.Peek, ref remote);

                error = System.Net.Sockets.SocketError.Success;

                ////If the receive was a success
                //if (available > 0)
                //{              

                do received += justRecieved = socket.ReceiveFrom(buffer.Array, buffer.Offset + received, buffer.Count - received, System.Net.Sockets.SocketFlags.None, ref remote);
                while (socket.IsNullOrDisposed() is false && received is 0 /*|| justRecieved > 0 && received + justRecieved < pmax && socket.Connected*/);

                ////Lookup the context to determine if the packet will fit
                //var context = GetContextBySocket(socket);

                ////If there was a context and packet cannot fit
                //if (context is not null && received > context.MaximumPacketSize)
                //{
                //    //Log the problem
                //    Media.Common.ILoggingExtensions.Log(Logger, ToString() + "@ReceiveData - Cannot fit packet in buffer");

                //    //Determine if there was enough data to determine if the packet was rtp or rtcp and indicate a failed reception
                //    //if (received > RFC3550.CommonHeaderBits.Size)
                //    //{
                //    //    //context.m_FailedRtcpReceptions++;

                //    //    //context.m_FailedRtpReceptions++;
                //    //}

                //    //remove the reference
                //    context = null;
                //}


                //Use the data received to parse and complete any received packets, should take a parseState
                /*using (var memory = new Common.MemorySegment(buffer.Array, buffer.Offset, received)) */
                //}

            }
            catch (System.Net.Sockets.SocketException se)
            {
                error = se.SocketErrorCode;
            }
            catch (System.Exception ex)
            {
                Common.ILoggingExtensions.LogException(Logger, ex);
            }

            //Under TCP use Framing to obtain the length of the packet as well as the context.
            if (received > 0)
            {
                if (tcp) return ProcessFrameData(buffer.Array, buffer.Offset, received, socket);
                else ParseAndHandleData(buffer, ref expectRtcp, ref expectRtp, ref received, ref received);
            }
            //Return the amount of bytes received from this operation
            return received;
        }

        /// <summary>
        /// Used to handle Tcp framing, this should be put on the TransportContext or it should allow a way for Transport to be handled, right now this is done in OnInterleavedData
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="offset"></param>
        /// <param name="count"></param>
        /// <param name="socket"></param>
        /// <returns></returns>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        protected internal /*virtual*/ int ProcessFrameData(byte[] buffer, int offset, int count, System.Net.Sockets.Socket socket)
        {
            if (count <= 0) return Common.Binary.Zero;

            //If there is no buffer use our own buffer.
            if (Media.Common.Extensions.Array.ArrayExtensions.IsNullOrEmpty(buffer))
            {
                if (Common.IDisposedExtensions.IsNullOrDisposed(m_Buffer)) return 0;

                buffer = m_Buffer.Array;
            }

            //Determine which TransportContext will receive the data incoming
            TransportContext relevent = null;

            //The channel of the data
            byte? frameChannel = default;

            //Get the length of the given buffer (Should actually use m_Buffer.Count when using our own buffer)
            int bufferLength = buffer.Length,
                //The indicates length of the data
                frameLength = Common.Binary.Zero,
                //The amount of data remaining in the buffer
                remainingInBuffer = count,
                //The amount of data received (which is already equal to what is remaining in the buffer)
                recievedTotal = remainingInBuffer;

            //Determine if Rtp or Rtcp is coming in or some other type (could be combined with expectRtcp and expectRtp is false)
            bool expectRtp = false, expectRtcp = false, incompatible = true, raisedEvent = false, jumbo = false, hasFrameHeader = false;

            //If anything remains on the socket the value will be calulcated.
            int remainingOnSocket = Common.Binary.Zero, remainingInFrame = remainingOnSocket;

            //TODO handle receiving when no $ and Channel is presenent... e.g. RFC4571
            //Would only be 2 then...

            int sessionRequired = TransportContexts.Any(tc => tc.MediaDescription.MediaProtocol.StartsWith("TCP")) ? 2 : InterleavedOverhead;

            //Todo, we allow a buffer to be given so we must also check if its changed to null...

            //Because it is not passed by 'ref' the changes to the array will not be seen if changed by `this` function, e.g. from the `caller`
            //however changes to the `array` from the `caller` WILL NOT be seen either
            //Functions calls which receive `Array` cannot exchange information this way unless they calls are retain the same `version` as passed from the `caller`

            int registerX, registerY;

            //While not disposed and there is data remaining (within the buffer)
            while (m_StopRequested is false &&
                Common.IDisposedExtensions.IsNullOrDisposed(m_Buffer) is false &&
                remainingInBuffer > Common.Binary.Zero &&
                offset >= m_Buffer.Offset &&
                Common.IDisposedExtensions.IsNullOrDisposed(this) is false)
            {
                ContinueParsing:
                //Assume not rtp or rtcp and that the data is compatible with the session
                hasFrameHeader = jumbo = expectRtp = expectRtcp = incompatible = false;

                //If a header can be read
                if (remainingInBuffer >= sessionRequired)
                {
                    //Determine if an event was raised each time there was at least the required amount of data.
                    raisedEvent = false;

                    //Parse the frameLength from the given buffer, take changes to the offset through the function.
                    //should give out the frame header length.
                    frameLength = ReadApplicationLayerFraming(ref remainingInBuffer, ref sessionRequired, ref offset, out frameChannel, out relevent, out raisedEvent, buffer);

                    //If the event was raised then the data belong to another protocol
                    if (raisedEvent)
                    {
                        //Move the offset
                        offset += frameLength;

                        //decrease what remains
                        remainingInBuffer -= frameLength;

#if DEBUG
                        Media.Common.ILoggingExtensions.Log(Logger, InternalId + "@ProcessFrameData - raisedEvent for frameLength: " + frameLength.ToString() + " remainingInBuffer=" + remainingInBuffer);
#endif

                        //Iterate again
                        continue;
                    }

                    //Assign jumbo, If a frame was found (Including the null packet)
                    if (frameLength >= Common.Binary.Zero)
                    {
                        //Determine if a frameHeader was found...
                        hasFrameHeader = true;

                        //Determine how large the packet is
                        jumbo = frameLength > bufferLength;

                        //If there WAS a context
                        if (Common.IDisposedExtensions.IsNullOrDisposed(relevent) is false)
                        {
                            ////Handle indepent framing, supposedly independent and interleaved are not allowed on the same connection...
                            ////We will see if that holds true and for how long...
                            //if (GetContextBySocket(socket).MediaDescription.MediaProtocol.StartsWith("TCP", StringComparison.OrdinalIgnoreCase))
                            //{
                            //    sessionRequired = 2;
                            //}

                            #region Verify FrameLength

                            //Verify minimum and maximum packet sizes allowed by context. (taking into account the amount of bytes in the ALF)
                            if (frameLength < relevent.MinimumPacketSize + sessionRequired ||
                                frameLength > relevent.MaximumPacketSize + sessionRequired)
                            {
                                //mark as incompatible
                                //incompatible = true;

                                Media.Common.ILoggingExtensions.Log(Logger, InternalId + "@ProcessFrameData - Buffer Exceeded Packet of " + frameLength + " for Channel " + frameChannel + " remainingInBuffer=" + remainingInBuffer);

                                //BufferExceeded => alow for resize.

                            }

                            //TODO Independent framing... (e.g. no $)[ only 4 bytes not 6 ]
                            //If all that remains is the frame header then receive more data. 6 comes from (InterleavedOverhead + CommonHeaderBits.Size)
                            //We need more data to be able to verify the frame.
                            if (remainingInBuffer <= sessionRequired + RFC3550.CommonHeaderBits.Size)
                            {
                                //Remove the context
                                relevent = null;

                                Media.Common.ILoggingExtensions.Log(Logger, InternalId + "@ProcessFrameData - (" + remainingInBuffer + ")" + ", Needs more data to inspect packet fields, frameLength = " + frameLength + " for Channel " + frameChannel + " remainingInBuffer=" + remainingInBuffer);

                                goto CheckRemainingData;

                                ////Only receive this many more bytes for now.
                                //remainingOnSocket = X - remainingInBuffer;

                                ////Receive the rest of the data indicated by frameLength. (Should probably only receive up to X more bytes then make another receive if needed)
                                //goto GetRemainingData;
                            }

                            #endregion

                            #region Verify Packet Headers

                            //Use CommonHeaderBits on the data after the Interleaved Frame Header
                            using (RFC3550.CommonHeaderBits common = new(buffer, offset + sessionRequired))
                            {
                                //Check the version...
                                incompatible = common.Version.Equals(relevent.Version) is false;

                                //If this is a valid context there must be at least a RtpHeader's worth of data in the buffer. 
                                //If this was a RtcpPacket with only 4 bytes it wouldn't have a ssrc and wouldn't be valid to be sent.
                                if (incompatible is false &&
                                    frameChannel.HasValue && frameChannel.Value.Equals(relevent.DataChannel) &&
                                    remainingInBuffer < Rtp.RtpHeader.Length + sessionRequired
                                    ||
                                    (frameChannel.HasValue && frameChannel.Value.Equals(relevent.ControlChannel) &&
                                    remainingInBuffer < Rtcp.RtcpHeader.Length + sessionRequired))
                                {
                                    //Remove the context
                                    relevent = null;

                                    //Mark as incompatible
                                    incompatible = true;

                                    goto EndUsingHeader;

                                    ////Only receive this many more bytes for now.
                                    //remainingOnSocket = 16 - remainingInBuffer;

                                    ////Receive the rest of the data indicated by frameLength. (Should probably only receive up to 6 more bytes then make another receive if needed)
                                    //goto GetRemainingData;
                                }


                                //Perform a set of checks and set weather or not Rtp or Rtcp was expected.                                  
                                if (incompatible is false)
                                {
                                    //Determine if the packet is Rtcp by looking at the found channel and the relvent control channel
                                    if (frameChannel.HasValue && frameChannel.Value.Equals(relevent.ControlChannel) && relevent.InDiscovery is false)
                                    {
                                        //Rtcp

                                        if (remainingInBuffer <= sessionRequired + Rtcp.RtcpHeader.Length)
                                        {
                                            //Remove the context
                                            relevent = null;

                                            goto CheckRemainingData;
                                        }

                                        //use a rtcp header to extract the information in the packet
                                        using (Rtcp.RtcpHeader header = new(buffer, offset + sessionRequired))
                                        {
                                            //Get the length in 'words' (by adding one)
                                            //A length of 0 means 1 word
                                            //A length of 65535 means only the header (no ssrc [or payload])
                                            ushort lengthInWordsPlusOne = (ushort)(header.LengthInWordsMinusOne + 1);

                                            //Store any rtcp length so we can verify its not 0 and then additionally ensure its value is not larger then the frameLength
                                            //Convert to bytes
                                            int rtcpLen = lengthInWordsPlusOne * 4;

                                            //Check that the supposed  amount of contained words is greater than or equal to the frame length conveyed by the application layer framing
                                            //it must also be larger than the buffer
                                            incompatible = rtcpLen >= frameLength || rtcpLen >= bufferLength;

                                            //if rtcpLen >= ushort.MaxValue the packet may possibly span multiple segments unless a large buffer is used.

                                            if (incompatible is false && //It was not already ruled incomaptible
                                                lengthInWordsPlusOne > 0 && //If there is supposed to be SSRC in the packet
                                                header.Size > Rtcp.RtcpHeader.Length && //The header ACTUALLY contains enough bytes to have a SSRC
                                                relevent.InDiscovery is false)//The remote context knowns the identity of the remote stream                                                 
                                            {
                                                //Determine if Rtcp is expected
                                                //Perform another lookup and check compatibility
                                                expectRtcp = (incompatible = Common.IDisposedExtensions.IsNullOrDisposed(GetContextBySourceId(header.SendersSynchronizationSourceIdentifier))) is false;
                                            }
                                        }
                                    }

                                    //May be mixing channels...
                                    if (expectRtcp is false/* && relevent.InDiscovery is false*/)
                                    {
                                        //Rtp
                                        if (remainingInBuffer <= sessionRequired + Rtp.RtpHeader.Length)
                                        {
                                            //Remove the context
                                            relevent = null;

                                            goto CheckRemainingData;
                                        }

                                        //the context by payload type is null is not discovering the identity check the SSRC.
                                        if (Common.IDisposedExtensions.IsNullOrDisposed(GetContextByPayloadType(common.RtpPayloadType)) is false /*&& relevent.InDiscovery is false*/)
                                        {
                                            using (Rtp.RtpHeader header = new(buffer, offset + sessionRequired))
                                            {
                                                //The context was obtained by the frameChannel
                                                //Use the SSRC to determine where it should be handled.
                                                //If there is no context the packet is incompatible
                                                expectRtp = (incompatible = Common.IDisposedExtensions.IsNullOrDisposed(GetContextBySourceId(header.SynchronizationSourceIdentifier))) is false;

                                                //(Could also check SequenceNumber to prevent duplicate packets from being processed.)

                                                ////Verify extensions (handled by ValidatePacket)
                                                //if (header.Extension)
                                                //{

                                                //}

                                            }
                                        }
                                        else incompatible = false;
                                    }
                                }
                                EndUsingHeader:
                                ;
                            }

                            #endregion
                        }

                        //If the frameLength is larger than the buffer all the data cannot fit
                        if (jumbo)
                        {
                            //If rtp or rtcp is expected check data
                            if (expectRtp || expectRtcp || frameChannel.HasValue && frameChannel.Value < TransportContexts.Count)
                            {
                                Media.Common.ILoggingExtensions.Log(Logger, InternalId + "ProcessFrameData - Large Packet of " + frameLength + " for Channel " + frameChannel.GetValueOrDefault() + " remainingInBuffer=" + remainingInBuffer);

                                //Could allow for the buffer to be replaced here for the remainder of this call only.

                                goto CheckRemainingData;
                            }
                        }
                        else goto CheckRemainingData;

                        //The packet was incompatible or larger than the buffer

                        //Determine how much we can move
                        registerX = frameLength > remainingInBuffer ? Common.Binary.Min(ref remainingInBuffer, ref sessionRequired) : frameLength;

                        //Media.Common.ILoggingExtensions.Log(Logger, InternalId + "ProcessFrameData Moving = " + toMove +", frameLength=" + frameLength + ", remainingInBuffer = " + remainingInBuffer);

                        //TODO It may be possible to let the event reciever known how much is available here.

                        //Indicate what was received if not already done
                        if (raisedEvent is false) OnOutOfBandData(buffer, offset, registerX);

                        //Move the offset
                        offset += registerX;

                        //Decrease by the length
                        remainingInBuffer -= registerX;

                        //Do another pass
                        continue;

                    }//else there was a frameLength of -1 this indicates there is not enough bytes for a header.
                }
                else//There is not enough data in the buffer as defined by sessionRequired.
                {
                    //unset the frameLength read
                    frameLength = -1;

                    //unset the context read
                    relevent = null;
                }

                //At this point there may be either less sessionRequired or not enough for a complete frame.
                CheckRemainingData:

                //See how many more bytes are required from the wire
                //If the frameLength is less than 0 AND there are less then or equal to sessionRequired remaining in the buffer
                remainingOnSocket = frameLength < 0 && remainingInBuffer <= sessionRequired ?
                    bufferLength - remainingInBuffer //Receive enough to complete the header or see another packet, whatever was ack'd will be available in the buffer.
                        : //Otherwise if the frameLength larger then what remains in the buffer allow for the buffer to be filled or nothing else remains.
                    frameLength > remainingInBuffer ? frameLength - remainingInBuffer : 0;

                //If there is anymore data remaining on the wire
                if (m_StopRequested is false && remainingOnSocket > 0 && socket is not null && Common.IDisposedExtensions.IsNullOrDisposed(this) is false)
                {
                    //Align the buffer if anything remains on the socket.
                    if (remainingOnSocket + offset + remainingInBuffer > bufferLength)
                    {
                        System.Array.Copy(buffer, offset, buffer, m_Buffer.Offset, remainingInBuffer);

                        //Set the correct offset either way.
                        offset = m_Buffer.Offset + remainingInBuffer;
                    }
                    else
                    {
                        offset += remainingInBuffer;
                    }

                    //Store the error if any
                    System.Net.Sockets.SocketError error = System.Net.Sockets.SocketError.Success;

                    //Get all the remaining data, todo, if not active must activate and join thread to hand off context.
                    while (m_StopRequested is false && remainingOnSocket > 0 && Common.IDisposedExtensions.IsNullOrDisposed(this) is false)
                    {
                        registerY = Media.Common.Extensions.Socket.SocketExtensions.AlignedReceive(buffer, offset, remainingOnSocket, socket, out error);

                        //Handle any error
                        switch (error)
                        {
                            case System.Net.Sockets.SocketError.WouldBlock:
                            case System.Net.Sockets.SocketError.SystemNotReady:
                            case System.Net.Sockets.SocketError.TooManyOpenSockets:
                            case System.Net.Sockets.SocketError.TryAgain:
                            case System.Net.Sockets.SocketError.TimedOut:
                                Media.Common.ILoggingExtensions.Log(Logger, InternalId + "ProcessFrameData - (" + error + ") remainingOnSocket " + remainingOnSocket + " for Channel " + frameChannel.Value + " remainingInBuffer=" + remainingInBuffer);

                                if (registerY > 0) break;

                                continue;
                            case System.Net.Sockets.SocketError.Success:
                                break;
                            case System.Net.Sockets.SocketError.Shutdown:
                            //If a socket error occured remove the context so no parsing occurs
                            default:
                                //OnTruncatedData
                                OnOutOfBandData(buffer, offset - remainingInBuffer, remainingInBuffer);

                                return recievedTotal;
                        }

                        //Decrease what is remaining from the wire by what was received
                        remainingOnSocket -= registerY;

                        //Move the offset
                        offset += registerY;

                        //Increment received
                        recievedTotal += registerY;

                        //Incrment remaining in buffer for what was received.
                        remainingInBuffer += registerY;
                    }

                    //Move back to where the frame started
                    offset -= remainingInBuffer;

                    //Go to the top of the loop to verify the data again.
                    if (jumbo is false) goto ContinueParsing;
                }

                //If the client is not disposed
                if (m_StopRequested is false && Common.IDisposedExtensions.IsNullOrDisposed(this) is false)
                {
                    //Calulcate how much remains
                    //remainingInFrame = jumbo ? frameLength - remainingInBuffer : frameLength;                    

                    //Todo, don't waste allocations on 0
                    //Parse the data in the buffer
                    using (Common.MemorySegment memory = hasFrameHeader ? new Common.MemorySegment(buffer, offset + sessionRequired, Common.Binary.Min(frameLength - sessionRequired, remainingInBuffer)) : new Common.MemorySegment(buffer, offset, remainingInBuffer))
                    {
                        registerX = Common.Binary.Max(Common.Binary.Zero, memory.Count);

                        //Don't use 0 as flow control here. Raising what potentially be multiple events would be dumb.
                        if (registerX is 0)
                        {
                            offset += sessionRequired;

                            remainingInBuffer -= sessionRequired;

                            continue;
                        }

                        //If there is a frame header than handle the data otherwise process as out of band.
                        if (hasFrameHeader)
                        {
                            ParseAndHandleData(memory, ref expectRtcp, ref expectRtp, ref registerX, ref remainingInFrame);
#if DEBUG
                            Media.Common.ILoggingExtensions.Log(Logger, InternalId + "ProcessFrameData - ParseAndHandleData");
#endif
                        }
                        else
                        {
                            OnOutOfBandData(memory.Array, memory.Offset, memory.Count);
#if DEBUG
                            Media.Common.ILoggingExtensions.Log(Logger, InternalId + "ProcessFrameData - OnOutOfBandData");
#endif
                        }

                        //Decrease remaining in buffer
                        remainingInBuffer -= registerX;

                        //Move the offset
                        offset += registerX;

                        //Ensure large frames are completely received by receiving the rest of the frame now.
                        if (jumbo /*frameLength > bufferLength*/)
                        {
                            //Remove the context
                            relevent = null;

                            //No more header...
                            hasFrameHeader = false;

                            //Determine how much remains
                            remainingOnSocket = frameLength - registerX;

                            //If there is anything left
                            if (remainingOnSocket > 0 /*&& expectRtcp || expectRtp*/)
                            {
                                //Set the new length of the frame based on the length of the buffer
                                //remainingInFrame = frameLength -= registerX;

                                remainingInFrame -= registerX;

                                frameLength -= registerX;

                                //Set what is remaining
                                remainingInBuffer = 0;

                                //Use all the buffer
                                offset = m_Buffer.Offset;

                                //still to big? (should not unset?_
                                //jumbo = remainingInFrame > bufferLength;

                                //go to receive it
                                goto CheckRemainingData;
                            }
                        }
                    }
                }
            }

            //Handle any data which remains if not already
            if (raisedEvent is false && offset >= 0 && remainingInBuffer > 0)
            {
                OnOutOfBandData(buffer, offset, remainingInBuffer);
            }

            //Return the number of bytes received
            return recievedTotal;
        }


        /// <summary>
        /// Parses the data in the buffer for valid Rtcp and Rtcp packet instances. (Expects no framing)
        /// </summary>
        /// <param name="memory">The memory to parse</param>
        /// <param name="from">The socket which received the data into memory and may be used for packet completion.</param>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        protected internal /*virtual*/ void ParseAndHandleData(Common.MemorySegment memory, ref bool parseRtcp, ref bool parseRtp, ref int remaining, ref int expected)
        {
            if (Common.IDisposedExtensions.IsNullOrDisposed(memory) || memory.Count is 0 || remaining <= 0) return;

            //handle demultiplex scenarios e.g. RFC5761
            if (parseRtcp.Equals(parseRtp) && memory.Count > RFC3550.CommonHeaderBits.Size)
            {
                //Double Negitive, Demux based on PayloadType? RFC5761?

                //Distinguishable RTP and RTCP Packets
                //https://tools.ietf.org/search/rfc5761#section-4

                //Observation 1) Rtp packets can only have a PayloadType from 64-95
                //However Rtcp Packets may also use PayloadTypes 72- 76.. (Reduced size...)

                //Observation 2) Rtcp Packets defined in RFC3550 Start at 200 (SR -> Goodbye) 204,
                // 209 - 223 is cited in the above as well as below
                //RTCP packet types in the ranges 1-191 and 224-254 SHOULD only be used when other values have been exhausted.

                using Media.RFC3550.CommonHeaderBits header = new(memory);

                //Could ensure version here to make reception more unified.

                //Just use the payload type to avoid confusion, payload types for Rtcp and Rtp cannot and should not overlap
                parseRtcp = (parseRtp = Media.Common.IDisposedExtensions.IsNullOrDisposed(GetContextByPayloadType(header.RtpPayloadType)) is false) is false;

                //Could also lookup the ssrc
            }

            //If the packet was truncated then it may be necessary to remove atleast the 'Padding' bit if it was set.

            //if(expected > remaining){...//OnTrunatedPacket(memory, bool rtp, bool rtcp, expected)}

            //Cache start, count and index
            int offset = memory.Offset, count = memory.Count, index = 0,
            //Calulcate remaining, take whatever is less
            mRemaining = remaining;

            //If rtcp should be parsed
            if (parseRtcp && mRemaining >= Rtcp.RtcpHeader.Length)
            {
                //Iterate the packets within the buffer, calling Dispose on each packet
                foreach (Rtcp.RtcpPacket rtcp in Rtcp.RtcpPacket.GetPackets(memory.Array, offset + index, Common.Binary.Min(ref mRemaining, ref count)))
                {
                    //Handle the packet further (could indicate truncated here)
                    HandleIncomingRtcpPacket(this, rtcp);

                    //Move the offset the length of the packet parsed
                    index += rtcp.Length;

                    mRemaining -= rtcp.Length;
                }
            }

            //Rtp:

            //If rtp is parsed
            if (parseRtp && mRemaining >= RtpHeader.Length)
            {
                //Use the packet to call Dispose.
                using RtpPacket rtp = new(memory.Array, offset + index, Common.Binary.Min(ref mRemaining, ref count));

                //Handle the packet further  (could indicate truncated here)
                HandleIncomingRtpPacket(this, rtp);

                //Move the index past the length of the packet
                index += rtp.Length;

                //Calculate the amount of octets remaining in the segment.
                mRemaining -= rtp.Length;
            }

            //If not all data was consumed
            if (mRemaining > 0)
            {
                Media.Common.ILoggingExtensions.Log(Logger, ToString() + "@ParseAndCompleteData - Remaining= " + mRemaining);

                OnOutOfBandData(memory.Array, offset + index, mRemaining);
            }

            return;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void HandleEvent()
        {
            //handle the event frame
            if (m_EventData.TryDequeue(out (RtpClient.TransportContext Context, Common.BaseDisposable Frame, bool Final, bool Received) tuple))
            {
                //If the item was already disposed then do nothing
                if (Common.IDisposedExtensions.IsNullOrDisposed(tuple.Frame)) return;

                //handle for received frames
                //todo, length may be more valuable than bool, - means in, positive is out
                if (tuple.Received && tuple.Frame is RtpFrame frame)
                {
                    ParallelRtpFrameChanged(frame, tuple.Context, tuple.Final);
                }
                else
                {
                    //Determine what type of packet
                    Common.IPacket what = tuple.Frame as Common.IPacket;

                    //handle the packet event
                    if (what is RtpPacket rtp)
                    {
                        if (tuple.Received)
                            ParallelRtpPacketRecieved(rtp, tuple.Context);
                        else
                            ParallelRtpPacketSent(rtp, tuple.Context);
                    }
                    else if (what is Rtcp.RtcpPacket rtcp)
                    {
                        if (tuple.Received)
                            ParallelRtcpPacketRecieved(rtcp, tuple.Context);
                        else
                            ParallelRtcpPacketSent(rtcp, tuple.Context);
                    }
                    else
                    {
                        ParallelOutOfBandData(what as Media.Common.Classes.PacketBase);
                    }

                    //Free whatever was used now that the event is handled.
                    //if (false == tuple.Frame.ShouldDispose) Common.BaseDisposable.SetShouldDispose(tuple.Frame, true, true);
                }
            }
        }

        /// <summary>
        /// Entry point of the m_EventThread. Handles dispatching events
        /// </summary>
        private void HandleEvents()
        {
            EventsStarted = System.DateTime.UtcNow;

            unchecked
            {
                Begin:
                try
                {
                    //While the main thread is active.
                    while (m_ThreadEvents)
                    {
                        //If the event is not set
                        if (m_EventReady.IsSet is false)
                        {
                            //Wait for the event signal half of the amount of time
                            if (m_EventReady.Wait(Common.Extensions.TimeSpan.TimeSpanExtensions.OneTick) is false)
                            {
                                //Todo, ThreadInfo.

                                //Check if not already below normal priority
                                if (System.Threading.Thread.CurrentThread.Priority is not System.Threading.ThreadPriority.Lowest)
                                {
                                    //Relinquish priority
                                    System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Lowest;
                                }
                            }
                        }

                        //Reset the event when all frames are dispatched
                        if (IsActive && m_EventData.IsEmpty)
                        {
                            m_EventReady.Reset();

                            while (IsActive && m_EventData.IsEmpty)
                                m_EventReady.Wait(WaitIntervalBetweenEvents);
                        }
                        else if (IsActive is false) break;

                        //Set priority
                        System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.BelowNormal;

                        //handle the event in waiting.
                        HandleEvent();
                    }
                }
                catch (System.Exception ex)
                {
                    Media.Common.ILoggingExtensions.Log(Logger, ToString() + "@HandleEvents: " + ex.Message);
                    Media.Common.ILoggingExtensions.LogException(Logger, ex);

                    goto Begin;
                }
            }
        }

        internal int m_SignalOffset = -1, m_SignalCount = -1;

        internal System.Net.Sockets.Socket m_SignalSocket;

        internal int DoSignalWork(/*ref out*/)
        {
            System.Threading.ThreadPriority existingPriority = System.Threading.Thread.CurrentThread.Priority;

            System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.AboveNormal;

            int recv = 0;

            //Todo, HandOff, process one receive
            while (m_SignalOffset > 0 && Common.IDisposedExtensions.IsNullOrDisposed(this) is false)
            {
                recv = ProcessFrameData(m_Buffer.Array, m_SignalOffset, m_SignalCount, m_SignalSocket);

                m_SignalOffset += recv;

                m_SignalCount -= recv;

                if (m_SignalCount <= 0)
                {
                    m_SignalOffset = m_SignalCount = -1;

                    m_SignalSocket = null;

                    break;
                }
            }

            System.Threading.Thread.CurrentThread.Priority = existingPriority;

            return recv;
        }

        /// <summary>
        /// Entry point of the m_WorkerThread. Handles sending out RtpPackets and RtcpPackets in buffer and handling any incoming RtcpPackets.
        /// Sends a Goodbye and exits if no packets are sent of received in a certain amount of time
        /// </summary>
        //[System.Security.SecurityCritical]
        private void SendReceieve()
        {
            Started = System.DateTime.UtcNow;

            //Don't worry about overflow.
            unchecked
            {

                Begin:

                bool critical = false;

                //Todo, use Socket and Threading structures from Concepts and make parallel as possible. have a flag where someone can indicate either based on proto or otherwise.

                try
                {
                    Media.Common.ILoggingExtensions.Log(Logger, ToString() + "@SendRecieve - Begin");

                    System.DateTime lastOperation = System.DateTime.UtcNow;

                    //Todo, HandOff
                    if (DoSignalWork() > 0) lastOperation = System.DateTime.UtcNow;

                    System.Net.Sockets.SocketError lastError = System.Net.Sockets.SocketError.SocketError;

                    bool shouldStop = IsUndisposed is false || m_StopRequested;

                    //Should keep error Count and if errorCount == TransportContexts.Count then return otherwise reset.

                    int receivedRtp = 0, receivedRtcp = 0;

                    bool duplexing, rtpEnabled, rtcpEnabled;

                    //Until aborted
                    while ((shouldStop = IsUndisposed is false || m_StopRequested) is false)
                    {
                        //Keep how much time has elapsed thus far
                        System.TimeSpan taken = System.DateTime.UtcNow - lastOperation;

                        //Todo
                        //use a local projection of GetTransportContexts()
                        //Use a sort by the last active context so that least active contexts go first

                        //set stop by all

                        //Stop if nothing has happed in at least the time required for sending and receiving on all contexts.
                        //shouldStop = GetTransportContexts().Where(tc => false == Common.IDisposedExtensions.IsNullOrDisposed(tc)).All(tc => tc.SendInterval > TimeSpan.Zero ? taken > tc.SendInterval + tc.ReceiveInterval : false);

                        #region Recieve Incoming Data

                        //See Todo, this only increases usage in most environments, however if you have > 1 GBPS and you really want to try...

                        ////System.Collections.ArrayList readSockets = new System.Collections.ArrayList();

                        ////System.Collections.ArrayList writeSockets = new System.Collections.ArrayList();

                        ////System.Collections.ArrayList errorSockets = new System.Collections.ArrayList();

                        //Loop each context, newly added contexts will be seen on each iteration
                        for (int i = 0; (shouldStop = IsUndisposed is false || m_StopRequested) is false && i < TransportContexts.Count; ++i)
                        {

                            //Todo, HandOff
                            if (DoSignalWork() > 0) lastOperation = System.DateTime.UtcNow;

                            ////readSockets.Clear();

                            ////writeSockets.Clear();

                            ////errorSockets.Clear();

                            //Obtain a context
                            TransportContext tc;

                            try
                            {
                                tc = TransportContexts[i];
                            }
                            catch (IndexOutOfRangeException)
                            {
                                continue;
                            }

                            //Check for a context which is able to receive data
                            if (Common.IDisposedExtensions.IsNullOrDisposed(tc)
                                //Active must be true
                                || tc.IsActive is false
                                //If the context does not have continious media it must only receive data for the duration of the media.
                                || tc.IsContinious is false && tc.TimeRemaining < System.TimeSpan.Zero
                                //There can't be a Goodbye sent or received
                                || tc.Goodbye is not null) continue;

                            //Receive Data on the RtpSocket and RtcpSocket, summize the amount of bytes received from each socket.

                            //Reset the error.
                            lastError = System.Net.Sockets.SocketError.SocketError;

                            //Ensure priority is above normal
                            System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Normal;

                            //Critical
                            System.Threading.Thread.BeginCriticalRegion();

                            critical = true;

                            //Rfphd
                            receivedRtp = 0;
                            receivedRtcp = 0;

                            duplexing = tc.IsDuplexing;

                            rtpEnabled = tc.IsRtpEnabled;

                            rtcpEnabled = tc.IsRtcpEnabled;

                            ////readSockets.Add(tc.RtpSocket);

                            ////readSockets.Add(tc.RtcpSocket);

                            ////writeSockets.Add(tc.RtpSocket);

                            ////writeSockets.Add(tc.RtcpSocket);

                            ////errorSockets.Add(tc.RtpSocket);

                            ////errorSockets.Add(tc.RtcpSocket);

                            //-1 may not poll forever, may return immediately. error [could] mean out of band inline
                            ////System.Net.Sockets.Socket.Select(readSockets, writeSockets, errorSockets, -1);

                            //Todo, handle timeouts?

                            //Determine how long to poll for, use 1 quarter of the entire interval
                            int usec = (int)tc.m_ReceiveInterval.TotalMicroseconds >> 4;

                            //If receiving Rtp and the socket is able to read
                            if (rtpEnabled &&
                                (shouldStop = IsUndisposed is false || m_StopRequested) is false
                                //Check if the socket can read data first or that data needs to be received
                                &&
                                (tc.LastRtpPacketReceived == System.TimeSpan.MinValue ||
                                 tc.LastRtpPacketReceived >= tc.m_ReceiveInterval) ||
                                tc.RtpSocket.Poll(usec, System.Net.Sockets.SelectMode.SelectRead))
                            {
                                //RtpTry:
                                //Receive RtpData
                                receivedRtp += ReceiveData(tc.RtpSocket, ref tc.RemoteRtp, out lastError, rtpEnabled, duplexing, tc.ContextMemory);

                                //Check if an error occured
                                if (receivedRtp is 0 || lastError is not System.Net.Sockets.SocketError.Success)
                                {
                                    //Increment for failed receptions
                                    ++tc.m_FailedRtpReceptions;

                                    //Log for the error
                                    Media.Common.ILoggingExtensions.Log(Logger, ToString() + "@SendRecieve RtpSocket - SocketError = " + lastError + " lastOperation = " + lastOperation + " taken = " + taken);

                                    if (taken >= tc.m_ReceiveInterval)
                                    {
                                        //Indicate the poll was not successful
                                        lastError = System.Net.Sockets.SocketError.TimedOut;

                                        Media.Common.ILoggingExtensions.Log(Logger, ToString() + "@SendRecieve - Unable to Poll RtpSocket in tc.m_ReceiveInterval = " + tc.ReceiveInterval + ", taken =" + taken);
                                    }
                                    //else
                                    //{
                                    //    usec *= 2;
                                    //    goto RtpTry;
                                    //}

                                    switch (lastError)
                                    {
                                        case System.Net.Sockets.SocketError.Success: lastOperation = System.DateTime.UtcNow; break;
                                        case System.Net.Sockets.SocketError.TimedOut:
                                            {
                                                //Stop receiving after enough timeouts, should have seperate fields for Send and Receive of each protocol...
                                                if (tc.m_FailedRtpReceptions > tc.RtpPacketsReceived) tc.IsRtpEnabled = false;
                                                break;
                                            }
                                        case System.Net.Sockets.SocketError.ConnectionReset:
                                        case System.Net.Sockets.SocketError.ConnectionAborted:
                                        case System.Net.Sockets.SocketError.AccessDenied:
                                            Media.Common.ILoggingExtensions.Log(Logger, ToString() + "@SendRecieve (Rtp)Deactivate");
                                            Deactivate();
                                            return;
                                    }
                                }
                            }

                            //if Rtcp is enabled
                            if (rtcpEnabled && (shouldStop = IsUndisposed is false || m_StopRequested) is false)
                            {
                                //Check if reports needs to be received (Sometimes data doesn't flow immediately)
                                bool needsToReceiveReports = tc.LastRtcpReportReceived.Equals(System.TimeSpan.MinValue) || tc.LastRtcpReportReceived >= tc.m_ReceiveInterval;

                                //The last report was never received or received longer ago then required
                                if (needsToReceiveReports
                                    //&& (readSockets.Contains(tc.RtcpSocket) || errorSockets.Contains(tc.RtcpSocket))
                                    //And the socket can read
                                    && tc.RtcpSocket.Poll(usec, System.Net.Sockets.SelectMode.SelectRead))
                                {
                                    //ReceiveRtcp Data
                                    receivedRtcp += ReceiveData(tc.RtcpSocket, ref tc.RemoteRtcp, out lastError, duplexing, rtcpEnabled, tc.ContextMemory);

                                    //Check if an error occured
                                    if (receivedRtcp is 0 || lastError is not System.Net.Sockets.SocketError.Success)
                                    {
                                        //Increment for failed receptions
                                        ++tc.m_FailedRtcpReceptions;

                                        //Log for the error
                                        Media.Common.ILoggingExtensions.Log(Logger, ToString() + "@SendRecieve RtcpSocket - SocketError = " + lastError + " lastOperation = " + lastOperation + " taken = " + taken);

                                        if (tc.HasAnyRecentActivity is false)
                                        {
                                            //Indicate the poll was not successful
                                            lastError = System.Net.Sockets.SocketError.TimedOut;

                                            //If data is not yet flowing the do not log.
                                            Media.Common.ILoggingExtensions.Log(Logger, ToString() + "@SendRecieve - No RecentActivity and Unable to Poll RtcpSocket, LastReportsReceived = " + tc.LastRtcpReportReceived + ", taken =" + taken);
                                        }

                                        switch (lastError)
                                        {
                                            case System.Net.Sockets.SocketError.Success: lastOperation = System.DateTime.UtcNow; break;
                                            case System.Net.Sockets.SocketError.TimedOut:
                                                {
                                                    //Stop receiving after enough timeouts, should have seperate fields for Send and Receive of each protocol...
                                                    if (tc.m_FailedRtcpReceptions > tc.RtcpPacketsReceived) tc.IsRtcpEnabled = false;
                                                    break;
                                                }
                                            case System.Net.Sockets.SocketError.ConnectionReset:
                                            case System.Net.Sockets.SocketError.ConnectionAborted:
                                            case System.Net.Sockets.SocketError.AccessDenied:
                                                Media.Common.ILoggingExtensions.Log(Logger, ToString() + "@SendRecieve (Rtcp)Deactivate");
                                                Deactivate();
                                                return;
                                        }
                                    }
                                }

                                //Try to send reports for the latest packets or a goodbye if inactive.
                                if (SendReports(tc, out lastError) || SendGoodbyeIfInactive(lastOperation, tc)) lastOperation = System.DateTime.UtcNow;
                            }
                            //else if (tc.HasSentRtcpWithinSendInterval is false && SendReports(tc, out lastError)) lastOperation = System.DateTime.UtcNow;
                        }

                        //if there was a socket error at the last stage
                        switch (lastError)
                        {
                            case System.Net.Sockets.SocketError.SocketError:
                            case System.Net.Sockets.SocketError.Success:
                                break;
                            default:
                                {
                                    //If there are no packets outgoing
                                    if ((m_OutgoingRtcpPackets.Count + m_OutgoingRtpPackets.Count) is 0)
                                    {

                                        //Just Take no action (leave Priority Normal)
                                        break;

                                        #region Unused, Throttle Priority when there are no outgoing packets.

                                        //System.Threading.Thread.CurrentThread.Priority = false == m_EventReady.Wait(Common.Extensions.TimeSpan.TimeSpanExtensions.OneTick) ? ThreadPriority.Normal : ThreadPriority.BelowNormal;

                                        //System.Threading.Thread.CurrentThread.Priority = m_EventReady.IsSet ? ThreadPriority.BelowNormal : ThreadPriority.Normal;

                                        //////Attempt to Halt and use the rest of the time slice, if no interrupt was received use BlowNormal
                                        ////if (false == System.Threading.Thread.Yield()) System.Threading.Thread.CurrentThread.Priority = ThreadPriority.Normal;
                                        ////else System.Threading.Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;

                                        #endregion

                                    }
                                    else
                                    {
                                        System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.AboveNormal;
                                    }

                                    break;
                                }
                        }

                        //Critical
                        System.Threading.Thread.EndCriticalRegion();

                        critical = false;

                        #endregion

                        //Todo, each context should have it's own thread or atleast its own outgoing packets to ensure not to much time is spent sending on each context. 

                        #region Handle Outgoing RtcpPackets

                        int remove = m_OutgoingRtcpPackets.Count;

                        if (remove > 0)
                        {
                            System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Highest;

                            System.Threading.Thread.BeginCriticalRegion();

                            critical = true;
                            //Todo, do a TakeWhile and sort by something which will allow packets which have different parties or channels.

                            //Try and send the lot of them
                            if (SendRtcpPackets(System.Linq.Enumerable.Take(m_OutgoingRtcpPackets, remove = m_OutgoingRtcpPackets.Count)) > 0)
                            {
                                lastOperation = System.DateTime.UtcNow;

                                //Remove what was attempted to be sent (don't try to send again)
                                m_OutgoingRtcpPackets.RemoveRange(0, remove);
                            }

                            System.Threading.Thread.EndCriticalRegion();

                            critical = false;

                            System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Normal;
                        }

                        #endregion

                        #region Handle Outgoing RtpPackets

                        remove = m_OutgoingRtpPackets.Count;

                        if (remove > 0)
                        {

                            System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Highest;

                            System.Threading.Thread.BeginCriticalRegion();

                            critical = true;

                            //Could check for timestamp more recent then packet at 0  on transporContext and discard...
                            //Send only A few at a time to share with rtcp

                            //If more than 1 thread is accessing this logic one could declare another varaible to compare what was supposed to be removed with what is actually being removed.
                            remove = 0;

                            //int? lastTimestamp;

                            //Take the list count to reduce exceptions
                            ////System.Linq.ParallelEnumerable.ForAll(System.Linq.ParallelEnumerable.AsParallel(m_OutgoingRtpPackets), packet =>
                            ////{
                            ////    //If already disposed
                            ////    if (Common.IDisposedExtensions.IsNullOrDisposed(packet))
                            ////    {
                            ////        ++remove;

                            ////        return;
                            ////    }

                            ////    //If the packet should dispose
                            ////    bool shouldDispose = packet.ShouldDispose;

                            ////    //prevent dispose
                            ////    if (shouldDispose) Common.BaseDisposable.SetShouldDispose(packet, false, false);

                            ////    //Get the context for the packet
                            ////    TransportContext sendContext = GetContextForPacket(packet);

                            ////    if (Common.IDisposedExtensions.IsNullOrDisposed(sendContext) || Common.IDisposedExtensions.IsNullOrDisposed(sendContext.Goodbye) is false)
                            ////    {
                            ////        ++remove;

                            ////        return;
                            ////    }

                            ////    //Send the packet using the context's SynchronizationSourceIdentifier
                            ////    if(SendRtpPacket(packet, sendContext, out lastError, sendContext.SynchronizationSourceIdentifier) >= packet.Length) lastOperation = System.DateTime.UtcNow;

                            ////    //Indicate to remove another packet
                            ////    ++remove;

                            ////    if (shouldDispose) Common.BaseDisposable.SetShouldDispose(packet, true, false);

                            ////    if (m_StopRequested) return;
                            ////});

                            //Taking all packets here is not good
                            //Should partition into packets for each context or have a max to send to prevent over sending.

                            TransportContext sendContext = null;

                            //System.Collections.Generic.Dictionary<int, TransportContext> lookup = new System.Collections.Generic.Dictionary<int, TransportContext>();

                            //foreach (TransportContext tc in TransportContexts) lookup.Add(tc.SynchronizationSourceIdentifier, tc);

                            for (int i = 0; i < m_OutgoingRtpPackets.Count; ++i)
                            {
                                //Get a packet
                                RtpPacket packet = m_OutgoingRtpPackets[i];

                                //If already disposed
                                if (Common.IDisposedExtensions.IsNullOrDisposed(packet))
                                {
                                    ++remove;

                                    continue;
                                }

                                //If the packet should dispose
                                bool shouldDispose = packet.ShouldDispose;

                                //prevent dispose
                                if (shouldDispose) Common.BaseDisposable.SetShouldDispose(packet, false, false);

                                //Get the context for the packet
                                if (sendContext is null || sendContext.SynchronizationSourceIdentifier != packet.SynchronizationSourceIdentifier)
                                {
                                    sendContext = GetContextForPacket(packet);

                                    //if (false == lookup.TryGetValue(packet.SynchronizationSourceIdentifier, out sendContext))
                                    //{
                                    //    sendContext = GetContextForPacket(packet);
                                    //    lookup.Add(packet.SynchronizationSourceIdentifier, sendContext);
                                    //}
                                }

                                if (Common.IDisposedExtensions.IsNullOrDisposed(sendContext) || sendContext.Goodbye is not null) goto Done;

                                //Send the packet using the context's SynchronizationSourceIdentifier
                                if (SendRtpPacket(packet, sendContext, out lastError, sendContext.SynchronizationSourceIdentifier) >= packet.Length /* && lastError == SocketError.Success*/)
                                {
                                    lastOperation = System.DateTime.UtcNow;
                                }

                                Done:
                                //Indicate to remove another packet
                                ++remove;

                                //Might want to force packet to not dispose until it's sent...
                                if (shouldDispose) Common.BaseDisposable.SetShouldDispose(packet, true, false);

                                //??? should be after this branch and the next...
                                if (m_StopRequested) break;

                                //Could also check timestamp in cases where marker is not being set
                                //if (lastTimestamp.HasValue && packet.Timestamp != lastTimestamp) break;
                                //lastTimestamp = packet.Timestamp;
                            }

                            //If any packets should be removed remove them now
                            if (remove > 0)
                            {
                                //Todo, Place in Confirm Stage for resending.
                                //Examine Rtcp RR and prune Confirming packets based on HighestExtendedSequenceNumber.
                                //When Feedback is used packets in Confirming stage can be re-transmitted easily.
                                //Packets outside of Confirming are either not yet sent or have been sent long ago, take care when honoring re-transmission

                                //Remove what was sent
                                m_OutgoingRtpPackets.RemoveRange(0, remove);
                            }

                            SendReports();

                            System.Threading.Thread.EndCriticalRegion();

                            critical = false;

                            System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Normal;

                            sendContext = null;
                        }

                        #endregion
                    }
                }
                catch (System.Exception ex)
                {
                    Media.Common.ILoggingExtensions.Log(Logger, ToString() + "@SendRecieve: " + ex.Message);
                    Media.Common.ILoggingExtensions.LogException(Logger, ex);

                    if (critical) System.Threading.Thread.EndCriticalRegion();

                    goto Begin;
                }
            }

            Media.Common.ILoggingExtensions.Log(Logger, ToString() + "@SendRecieve - Exit");
        }

        #endregion

        public bool TrySetLogger(Media.Common.ILogging logger)
        {
            //Never store the ref if disposed.
            if (Common.IDisposedExtensions.IsNullOrDisposed(this)) return false;

            Logger = logger;

            return true;
        }

        public bool TryGetLogger(out Media.Common.ILogging logger)
        {
            //Was ensuring the instance would never get retrieved..

            //if (Common.IDisposedExtensions.IsNullOrDisposed(this))
            //{
            //    logger = null;

            //    return false;
            //}

            logger = Logger;

            //return true;

            return false == Common.IDisposedExtensions.IsNullOrDisposed(this);
        }
    }
}