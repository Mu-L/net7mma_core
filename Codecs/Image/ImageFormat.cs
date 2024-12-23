﻿using Media.Codec;
using Media.Codecs.Image;
using System;
using System.Linq;
using System.Text;

namespace Media.Codecs.Image
{
    /// <summary>
    /// Represents an image format with various properties and methods for creating specific formats.
    /// Can be used as a base for a ColorSpace implementation
    /// </summary>
    public class ImageFormat : Codec.MediaFormat
    {
        #region Statics

        public const byte AlphaChannelId = (byte)'a';

        public const byte PreMultipliedAlphaChannelId = (byte)'p';

        //Possibly a type which has multiplied and normal types... 
        //public const byte MixedAlphaChannelId = (byte)'@';

        public const byte DeltaChannelId = (byte)'d';

        //

        public const byte LumaChannelId = (byte)'l';

        public const byte ChromaMajorChannelId = (byte)'u';

        public const byte ChromaMinorChannelId = (byte)'v';

        //

        public const byte RedChannelId = (byte)'r';

        public const byte GreenChannelId = (byte)'g';

        public const byte BlueChannelId = (byte)'b';

        //

        public const byte PaletteChannelId = (byte)'p';

        //Printing...

        public const byte CyanChannelId = (byte)'c';

        public const byte MagentaChannelId = (byte)'m';

        public const byte YellowChannelId = (byte)'y';

        //Key
        public const byte KChannelId = (byte)'k';

        //

        //CIE

        public const byte LChannelId = (byte)'L';

        public const byte AChannelId = (byte)'A';

        public const byte BChannelId = (byte)'B';

        //

        public const byte XChannelId = (byte)'X';

        public const byte YChannelId = (byte)'Y';

        public const byte ZChannelId = (byte)'Z';

        //Functions for reading lines are in the type which corresponds, e.g. Image.

        //Could have support here for this given a MediaBuffer and forced / known format...

        #endregion

        #region Known Image Formats

        public static ImageFormat WithoutAlphaComponent(ImageFormat other)
        {
            return new ImageFormat(other.ByteOrder, other.DataLayout, other.Components.Where(c => c.Id != AlphaChannelId));
        }

        public static ImageFormat WithProceedingAlphaComponent(ImageFormat other, int sizeInBits)
        {
            return new ImageFormat(other.ByteOrder, other.DataLayout, other.Components.Where(c => c.Id != AlphaChannelId).Concat(Common.Extensions.Linq.LinqExtensions.Yield(new Codec.MediaComponent(AlphaChannelId, sizeInBits))));
        }

        public static ImageFormat WithPreceedingAlphaComponent(ImageFormat other, int sizeInBits)
        {
            return new ImageFormat(other.ByteOrder, other.DataLayout, Common.Extensions.Linq.LinqExtensions.Yield(new Codec.MediaComponent(AlphaChannelId, sizeInBits)).Concat(other.Components.Where(c => c.Id != AlphaChannelId)));
        }

        public static ImageFormat Binary(int bitsPerComponent) //Bayer / Binary 
        {
            return new ImageFormat(Common.Binary.ByteOrder.Little, Codec.DataLayout.Packed, new Codec.MediaComponent[]
            {
                new(DeltaChannelId, bitsPerComponent)
            });
        }

        public static ImageFormat Monochrome(int bitsPerComponent)
        {
            return new ImageFormat(Common.Binary.ByteOrder.Little, Codec.DataLayout.Packed, new Codec.MediaComponent[]
            {
                new(LumaChannelId, bitsPerComponent)
            });
        }

        public static ImageFormat Palette(int bitsPerComponent)
        {
            return new ImageFormat(Common.Binary.ByteOrder.Little, Codec.DataLayout.Packed, new Codec.MediaComponent[]
            {
                new(PaletteChannelId, bitsPerComponent)
            });
        }

        public static ImageFormat RGB(int bitsPerComponent, Common.Binary.ByteOrder byteOrder = Common.Binary.ByteOrder.Little, Codec.DataLayout dataLayout = Codec.DataLayout.Packed)
        {
            return new ImageFormat(byteOrder, dataLayout, new Codec.MediaComponent[]
            {
                new(RedChannelId, bitsPerComponent),
                new(GreenChannelId, bitsPerComponent),
                new(BlueChannelId, bitsPerComponent)
            });
        }

        public static ImageFormat ARGB(int bitsPerComponent, Common.Binary.ByteOrder byteOrder = Common.Binary.ByteOrder.Little, Codec.DataLayout dataLayout = Codec.DataLayout.Packed, bool premultipliedAlpha = false)
        {
            return new ImageFormat(byteOrder, dataLayout, new Codec.MediaComponent[]
            {
                new(premultipliedAlpha ? PreMultipliedAlphaChannelId :AlphaChannelId, bitsPerComponent),
                new(RedChannelId, bitsPerComponent),
                new(GreenChannelId, bitsPerComponent),
                new(BlueChannelId, bitsPerComponent)
            });
        }

        public static ImageFormat RGBA(int bitsPerComponent, Common.Binary.ByteOrder byteOrder = Common.Binary.ByteOrder.Little, Codec.DataLayout dataLayout = Codec.DataLayout.Packed, bool premultipliedAlpha = false)
        {
            return new ImageFormat(byteOrder, dataLayout, new Codec.MediaComponent[]
            {
                new(RedChannelId, bitsPerComponent),
                new(GreenChannelId, bitsPerComponent),
                new(BlueChannelId, bitsPerComponent),
                new(premultipliedAlpha ? PreMultipliedAlphaChannelId : AlphaChannelId, bitsPerComponent)
            });
        }

        public static ImageFormat BGR(int bitsPerComponent, Common.Binary.ByteOrder byteOrder = Common.Binary.ByteOrder.Little, Codec.DataLayout dataLayout = Codec.DataLayout.Packed)
        {
            return new ImageFormat(byteOrder, dataLayout, new Codec.MediaComponent[]
            {
                new(BlueChannelId, bitsPerComponent),
                new(GreenChannelId, bitsPerComponent),
                new(RedChannelId, bitsPerComponent)
            });
        }

        public static ImageFormat BGRA(int bitsPerComponent, Common.Binary.ByteOrder byteOrder = Common.Binary.ByteOrder.Little, Codec.DataLayout dataLayout = Codec.DataLayout.Packed, bool premultipliedAlpha = false)
        {
            return new ImageFormat(byteOrder, dataLayout, new Codec.MediaComponent[]
            {
                new(BlueChannelId, bitsPerComponent),
                new(GreenChannelId, bitsPerComponent),
                new(RedChannelId, bitsPerComponent),
                new(premultipliedAlpha ? PreMultipliedAlphaChannelId : AlphaChannelId, bitsPerComponent)
            });
        }

        public static ImageFormat ABGR(int bitsPerComponent, Common.Binary.ByteOrder byteOrder = Common.Binary.ByteOrder.Little, Codec.DataLayout dataLayout = Codec.DataLayout.Packed, bool premultipliedAlpha = false)
        {
            return new ImageFormat(byteOrder, dataLayout, new Codec.MediaComponent[]
            {
                new(premultipliedAlpha ? PreMultipliedAlphaChannelId : AlphaChannelId, bitsPerComponent),
                new(BlueChannelId, bitsPerComponent),
                new(GreenChannelId, bitsPerComponent),
                new(RedChannelId, bitsPerComponent)
            });
        }

        public static ImageFormat YUV(int bitsPerComponent, Common.Binary.ByteOrder byteOrder = Common.Binary.ByteOrder.Little, Codec.DataLayout dataLayout = Codec.DataLayout.Packed)
        {
            //Uglier version of the constructor
            //public static readonly ImageFormat YUV = new ImageFormat(Common.Binary.ByteOrder.Little, Codec.DataLayout.Packed, 3, 8, new byte[] { LumaChannelId, ChromaMajorChannelId, ChromaMinorChannelId });
            return new ImageFormat(byteOrder, dataLayout, new Codec.MediaComponent[]
            {
                new(LumaChannelId, bitsPerComponent),
                new(ChromaMajorChannelId, bitsPerComponent),
                new(ChromaMinorChannelId, bitsPerComponent)
            });
        }

        public static ImageFormat YUVA(int bitsPerComponent, Common.Binary.ByteOrder byteOrder = Common.Binary.ByteOrder.Little, Codec.DataLayout dataLayout = Codec.DataLayout.Packed, bool premultipliedAlpha = false)
        {
            return new ImageFormat(byteOrder, dataLayout, new Codec.MediaComponent[]
            {
                new(LumaChannelId, bitsPerComponent),
                new(ChromaMajorChannelId, bitsPerComponent),
                new(ChromaMinorChannelId, bitsPerComponent),
                new(premultipliedAlpha ? PreMultipliedAlphaChannelId : AlphaChannelId, bitsPerComponent)
            });
        }

        public static ImageFormat AYUV(int bitsPerComponent, Common.Binary.ByteOrder byteOrder = Common.Binary.ByteOrder.Little, Codec.DataLayout dataLayout = Codec.DataLayout.Packed, bool premultipliedAlpha = false)
        {
            return new ImageFormat(byteOrder, dataLayout, new Codec.MediaComponent[]
            {
                new(premultipliedAlpha ? PreMultipliedAlphaChannelId : AlphaChannelId, bitsPerComponent),
                new(LumaChannelId, bitsPerComponent),
                new(ChromaMajorChannelId, bitsPerComponent),
                new(ChromaMinorChannelId, bitsPerComponent)
            });
        }

        public static ImageFormat VUY(int bitsPerComponent, Common.Binary.ByteOrder byteOrder = Common.Binary.ByteOrder.Little, Codec.DataLayout dataLayout = Codec.DataLayout.Packed)
        {
            return new ImageFormat(byteOrder, dataLayout, new Codec.MediaComponent[]
            {
                new(ChromaMinorChannelId, bitsPerComponent),
                new(ChromaMajorChannelId, bitsPerComponent),
                new(LumaChannelId, bitsPerComponent)
            });
        }

        public static ImageFormat VUYA(int bitsPerComponent, Common.Binary.ByteOrder byteOrder = Common.Binary.ByteOrder.Little, Codec.DataLayout dataLayout = Codec.DataLayout.Packed, bool premultipliedAlpha = false)
        {
            return new ImageFormat(byteOrder, dataLayout, new Codec.MediaComponent[]
            {
                new(ChromaMinorChannelId, bitsPerComponent),
                new(ChromaMajorChannelId, bitsPerComponent),
                new(LumaChannelId, bitsPerComponent),
                new(premultipliedAlpha ? PreMultipliedAlphaChannelId : AlphaChannelId, bitsPerComponent)
            });
        }

        public static ImageFormat AVUY(int bitsPerComponent, Common.Binary.ByteOrder byteOrder = Common.Binary.ByteOrder.Little, Codec.DataLayout dataLayout = Codec.DataLayout.Packed, bool premultipliedAlpha = false)
        {
            return new ImageFormat(byteOrder, dataLayout, new Codec.MediaComponent[]
            {
                new(AlphaChannelId, bitsPerComponent),
                new(ChromaMinorChannelId, bitsPerComponent),
                new(ChromaMajorChannelId, bitsPerComponent),
                new(premultipliedAlpha ? PreMultipliedAlphaChannelId : LumaChannelId, bitsPerComponent)
            });
        }

        public static ImageFormat CMYK(int bitsPerComponent, Common.Binary.ByteOrder byteOrder = Common.Binary.ByteOrder.Little, Codec.DataLayout dataLayout = Codec.DataLayout.Planar)
        {
            return new ImageFormat(byteOrder, dataLayout, new Codec.MediaComponent[] {
                new(CyanChannelId, bitsPerComponent),
                new(MagentaChannelId, bitsPerComponent),
                new(YellowChannelId, bitsPerComponent),
                new(KChannelId, bitsPerComponent)
            });
        }

        public static ImageFormat CMYKA(int bitsPerComponent, Common.Binary.ByteOrder byteOrder = Common.Binary.ByteOrder.Little, Codec.DataLayout dataLayout = Codec.DataLayout.Planar, bool premultipliedAlpha = false)
        {
            return new ImageFormat(byteOrder, dataLayout, new Codec.MediaComponent[] {
                new(CyanChannelId, bitsPerComponent),
                new(MagentaChannelId, bitsPerComponent),
                new(YellowChannelId, bitsPerComponent),
                new(KChannelId, bitsPerComponent),
                new(premultipliedAlpha ? PreMultipliedAlphaChannelId : AlphaChannelId, bitsPerComponent)
            });
        }

        public static ImageFormat XYZ(int bitsPerComponent, Common.Binary.ByteOrder byteOrder = Common.Binary.ByteOrder.Little, Codec.DataLayout dataLayout = Codec.DataLayout.Planar)
        {
            return new ImageFormat(byteOrder, dataLayout, [
                new(XChannelId, bitsPerComponent),
                new(YChannelId, bitsPerComponent),
                new(ZChannelId, bitsPerComponent),
            ]);
        }

        public static ImageFormat LAB(int bitsPerComponent, Common.Binary.ByteOrder byteOrder = Common.Binary.ByteOrder.Little, Codec.DataLayout dataLayout = Codec.DataLayout.Planar)
        {
            return new ImageFormat(byteOrder, dataLayout, [
                new(LChannelId, bitsPerComponent),
                new(AChannelId, bitsPerComponent),
                new(BChannelId, bitsPerComponent),
            ]);
        }

        //Supports 565 formats... etc.

        public static ImageFormat VariableYUV(int[] sizes, Common.Binary.ByteOrder byteOrder = Common.Binary.ByteOrder.Little, Codec.DataLayout dataLayout = Codec.DataLayout.Packed)
        {
            return new ImageFormat(byteOrder, dataLayout, new Codec.MediaComponent[]
            {
                new(LumaChannelId, sizes[0]),
                new(ChromaMajorChannelId, sizes[1]),
                new(ChromaMinorChannelId, sizes[2])
            });
        }

        public static ImageFormat VariableRGB(int[] sizes, Common.Binary.ByteOrder byteOrder = Common.Binary.ByteOrder.Little, Codec.DataLayout dataLayout = Codec.DataLayout.Packed)
        {
            return new ImageFormat(byteOrder, dataLayout, new Codec.MediaComponent[]
            {
                new(RedChannelId, sizes[0]),
                new(GreenChannelId, sizes[1]),
                new(BlueChannelId, sizes[2])
            });
        }

        //Those formats used by the Variable functions.

        public static ImageFormat RGB_565(Common.Binary.ByteOrder byteOrder = Common.Binary.ByteOrder.Little, Codec.DataLayout dataLayout = Codec.DataLayout.Packed)
        {
            return new ImageFormat(byteOrder, dataLayout, new Codec.MediaComponent[]
            {
                new(RedChannelId, 5),
                new(GreenChannelId, 6),
                new(BlueChannelId, 5)
            });
        }

        //32 bit -> 2 bit alpha 10 bit r, g, b
        public static ImageFormat ARGB_230(Common.Binary.ByteOrder byteOrder = Common.Binary.ByteOrder.Little, Codec.DataLayout dataLayout = Codec.DataLayout.Packed)
        {
            return new ImageFormat(byteOrder, dataLayout, new Codec.MediaComponent[]
            {
                new(AlphaChannelId, 2),
                new(RedChannelId, 10),
                new(GreenChannelId, 10),
                new(BlueChannelId, 10)
            });
        }

        public static ImageFormat YUV_565(Common.Binary.ByteOrder byteOrder = Common.Binary.ByteOrder.Little, Codec.DataLayout dataLayout = Codec.DataLayout.Packed)
        {
            return new ImageFormat(byteOrder, dataLayout, new Codec.MediaComponent[]
            {
                new(LumaChannelId, 5),
                new(ChromaMajorChannelId, 6),
                new(ChromaMinorChannelId, 5)
            });
        }

        public static ImageFormat WithSubSampling(ImageFormat other, int[] sampling)
        {
            //if (System.Linq.Enumerable.SequenceEqual(other.Widths, sampling) && System.Linq.Enumerable.SequenceEqual(other.Heights, sampling)) return other;

            return new ImageFormat(other, sampling);
        }

        public static ImageFormat WithSubSampling(ImageFormat other, int[] widthSampling, int[] heightSampling)
        {
            //if (System.Linq.Enumerable.SequenceEqual(other.Widths, widthSampling) && System.Linq.Enumerable.SequenceEqual(other.Heights, heightSampling)) return other;

            return new ImageFormat(other, widthSampling, heightSampling);
        }

        public static ImageFormat Packed(ImageFormat other)
        {
            return new ImageFormat(Codec.MediaFormat.Packed(other));
        }

        public static ImageFormat Planar(ImageFormat other)
        {
            return new ImageFormat(Codec.MediaFormat.Planar(other));
        }

        public static ImageFormat SemiPlanar(ImageFormat other)
        {
            return new ImageFormat(Codec.MediaFormat.SemiPlanar(other));
            //return new ImageFormat(other.ByteOrder, Codec.DataLayout.SemiPlanar, other.Components);
        }

        #endregion

        #region Fields

        public int[] HorizontalSamplingFactors, VerticalSamplingFactors;

        #endregion

        #region Constructors

        public ImageFormat(Common.Binary.ByteOrder byteOrder, Codec.DataLayout dataLayout, int components, int bitsPerComponent, byte[] componentIds)
            : base(Codec.MediaType.Image, byteOrder, dataLayout, components, bitsPerComponent, componentIds)
        {
            //No sub sampling
            VerticalSamplingFactors = HorizontalSamplingFactors = new int[components];
        }

        public ImageFormat(Common.Binary.ByteOrder byteOrder, Codec.DataLayout dataLayout, int components, int[] componentSizes, byte[] componentIds)
            : base(Codec.MediaType.Image, byteOrder, dataLayout, components, componentSizes, componentIds)
        {
            //No sub sampling
            VerticalSamplingFactors = HorizontalSamplingFactors = new int[components];
        }

        public ImageFormat(Common.Binary.ByteOrder byteOrder, Codec.DataLayout dataLayout, System.Collections.Generic.IEnumerable<Codec.MediaComponent> components)
            : base(Codec.MediaType.Image, byteOrder, dataLayout, components)
        {
            //No sub sampling
            VerticalSamplingFactors = HorizontalSamplingFactors = new int[Components.Length];
        }

        public ImageFormat(Common.Binary.ByteOrder byteOrder, Codec.DataLayout dataLayout, params Codec.MediaComponent[] components)
            : base(Codec.MediaType.Image, byteOrder, dataLayout, components)
        {
            //No sub sampling
            VerticalSamplingFactors = HorizontalSamplingFactors = new int[Components.Length];
        }

        public ImageFormat(ImageFormat other, params Codec.MediaComponent[] components)
            : base(other, other.ByteOrder, other.DataLayout, components)
        {
            HorizontalSamplingFactors = other.HorizontalSamplingFactors;

            VerticalSamplingFactors = other.VerticalSamplingFactors;
        }

        public ImageFormat(ImageFormat other, int[] sampling, params Codec.MediaComponent[] components)
            : base(other, other.ByteOrder, other.DataLayout, components) //: this(other,  components)
        {
            if (sampling is null) throw new System.ArgumentNullException("sampling");

            if (sampling.Length < Components.Length) throw new System.ArgumentOutOfRangeException("sampling", "Must have the same amount of elements as Components");

            //This needs to be able to reflect 4:4:4 or less
            //This is how this needs to look.

            //Sub Sampling | int | Example
            //           4 |   0 | 8 >> 0 = 8
            //           2 |   1 | 8 >> 1 = 4
            //           1 |   2 | 8 >> 2 = 2
            //        0.25 |   3 | 8 >> 3 = 1
            //           0 |   -1| skip

            HorizontalSamplingFactors = VerticalSamplingFactors = sampling;
        }

        public ImageFormat(ImageFormat other, int[] widths, int[] heights, params Codec.MediaComponent[] components)
            : base(other, other.ByteOrder, other.DataLayout, components) //: this(other,  components)
        {
            if (widths is null) throw new System.ArgumentNullException("widths");

            if (widths.Length < Components.Length) throw new System.ArgumentOutOfRangeException("widths", "Must have the same amount of elements as Components");

            if (heights is null) throw new System.ArgumentNullException("heights");

            if (heights.Length < Components.Length) throw new System.ArgumentOutOfRangeException("widths", "Must have the same amount of elements as Components");

            HorizontalSamplingFactors = widths;

            VerticalSamplingFactors = heights;
        }

        public ImageFormat(Codec.MediaFormat format)
            : base(format)
        {
            if (format is null) throw new System.ArgumentNullException("format");

            if (format.MediaType != Codec.MediaType.Image) throw new System.ArgumentException("format.MediaType", "Must be Codec.MediaType.Image.");
        }

        #endregion

        #region Properties

        public bool IsSubSampled { get { return HorizontalSamplingFactors.Any(c => c > 0) || VerticalSamplingFactors.Any(h => h > 0); } }

        public Codec.MediaComponent AlphaComponent { get { return GetComponentById(AlphaChannelId); } }

        public bool HasAlphaComponent { get { return AlphaComponent is not null; } }

        public bool IsPremultipliedWithAlpha
        {
            get
            {
                Codec.MediaComponent alphaComponent = AlphaComponent;
                return alphaComponent is not null && alphaComponent.Id == PreMultipliedAlphaChannelId;
            }
        }

        public string FormatString => Encoding.UTF8.GetString(Components.Select(c => c.Id).ToArray());

        #endregion

        #region Methods

        public override bool Equals(object obj)
            => obj is ImageFormat other && Equals(other);

        public bool Equals(ImageFormat imageFormat)
        {
            if (ReferenceEquals(this, imageFormat)) return true;
            return imageFormat.Components.SequenceEqual(imageFormat.Components) &&
                HorizontalSamplingFactors.SequenceEqual(imageFormat.HorizontalSamplingFactors) &&
                VerticalSamplingFactors.SequenceEqual(imageFormat.VerticalSamplingFactors);
        }

        public override int GetHashCode()
            => HashCode.Combine(Components, HorizontalSamplingFactors, VerticalSamplingFactors);

        #endregion
    }

    //Marked for removal
    ///// <summary>
    ///// Describes a PixelFormat
    ///// </summary>
    //public class PixelFormat
    //{
    //    #region KnownFormats

    //    public static PixelFormat YUV420p = new PixelFormat("yuv420p", 3, 1, 1, PixelFormatFlags.Planar,
    //        new ComponentDescriptor(0, 0, 1, 0, 7), 
    //        new ComponentDescriptor(1, 0, 1, 0, 7), 
    //        new ComponentDescriptor(2, 0, 1, 0, 7));

    //    public static PixelFormat YUYV422 = new PixelFormat("yuyv422", 3, 1, 0, PixelFormatFlags.None,
    //        new ComponentDescriptor(0, 1, 1, 0, 7),
    //        new ComponentDescriptor(0, 3, 2, 0, 7),
    //        new ComponentDescriptor(0, 3, 4, 0, 7));

    //    public static PixelFormat YVYU422 = new PixelFormat("yvyu422", 3, 1, 0, PixelFormatFlags.None,
    //        new ComponentDescriptor(0, 1, 1, 0, 7),
    //        new ComponentDescriptor(0, 3, 2, 0, 7),
    //        new ComponentDescriptor(0, 3, 4, 0, 7));

    //    public static PixelFormat RGB24 = new PixelFormat("rgb24", 3, 0, 0, PixelFormatFlags.RGB,
    //        new ComponentDescriptor(0, 2, 1, 0, 7),
    //        new ComponentDescriptor(0, 2, 2, 0, 7),
    //        new ComponentDescriptor(0, 2, 3, 0, 7));

    //    public static PixelFormat BGR24 = new PixelFormat("bgr24", 3, 0, 0, PixelFormatFlags.RGB,
    //       new ComponentDescriptor(0, 2, 1, 0, 7),
    //       new ComponentDescriptor(0, 2, 2, 0, 7),
    //       new ComponentDescriptor(0, 2, 3, 0, 7));

    //    #endregion

    //    #region Fields

    //    /// <summary>
    //    /// The name of the format
    //    /// </summary>
    //    public readonly string Name;

    //    /// <summary>
    //    /// The number of components in the format
    //    /// </summary>
    //    public readonly int Components;

    //    /// <summary>
    //    /// Amount to shift the luma width right to find the chroma width.
    //    /// </summary>
    //    public readonly int Log2ChromaWidth;

    //    /// <summary>
    //    /// Amount to shift the luma height right to find the chroma height.
    //    /// </summary>
    //    public readonly int Log2ChromaHeight;

    //    /// <summary>
    //    /// Other information
    //    /// </summary>
    //    public readonly PixelFormatFlags Flags;

    //    /// <summary>
    //    /// Parameters that describe how pixels are packed. 
    //    /// If the format has chroma components, they must be stored in ComponentDescriptions[1] and ComponentDescriptions[2].
    //    /// </summary>
    //    public ComponentDescriptor[] ComponentDescriptions;

    //    #endregion

    //    #region Constructor

    //    /// <summary>
    //    /// Constructs a new PixelFormat with the given configuration
    //    /// </summary>
    //    /// <param name="name"></param>
    //    /// <param name="numberOfComponents"></param>
    //    /// <param name="log2ChromaWidth"></param>
    //    /// <param name="log2ChromaHeight"></param>
    //    /// <param name="flags"></param>
    //    public PixelFormat(string name, int numberOfComponents, int log2ChromaWidth, int log2ChromaHeight, PixelFormatFlags flags, params ComponentDescriptor[] components)
    //    {
    //        if (string.IsNullOrWhiteSpace(name)) throw new System.ArgumentException("name", "Cannot be null or consist only of Whitespace.");
    //        Name = name;

    //        if (numberOfComponents <= 0) throw new System.ArgumentException("numberOfComponents", "Must be > than 0");
    //        Components = numberOfComponents;

    //        Log2ChromaHeight = log2ChromaHeight;

    //        Log2ChromaWidth = log2ChromaWidth;

    //        if (components is null || components.Length < Components) throw new System.ArgumentException("components", "Must be present and have the length indicated by numberOfComponents.");
    //        ComponentDescriptions = components;
    //    }

    //    #endregion
    //}

    //[Flags]
    //public enum PixelFormatFlags : byte
    //{
    //    None = 0,
    //    BigEndian = 1,
    //    Pal = 2,
    //    BitStream = 4,
    //    HWAccel = 8,
    //    Planar = 16,
    //    RGB = 32,//Packed
    //    PseudoPal = 64,
    //    Alpha = 128
    //}

    //public static class Extensions
    //{
    //    public static bool HasAlpha(this PixelFormat pf)
    //    {
    //        return pf is not null && pf.Flags.HasFlag(PixelFormatFlags.Alpha);
    //    }

    //    public static bool IsPlanar(this PixelFormat pf)
    //    {
    //        return pf is not null && pf.Flags.HasFlag(PixelFormatFlags.Planar);
    //    }

    //    public static bool IsPacked(this PixelFormat pf)
    //    {
    //        return false == IsPlanar(pf);
    //    }

    //    public static int NumberOfPlanes(this PixelFormat pf)
    //    {
    //        return pf.Components;
    //        //return pf.ComponentDescriptions.Length;
    //        //return pf.ComponentDescriptions.Sum(p => p.Plane > 0 ? 1 : 0);
    //    }

    //Basically all this supports and Read and Write API
    //A more managed solution would be IEnumerable<MemorySegment> or IEnumerable<byte[]> which would enumerate the lines for you

    //    public static void ReadImageLine(byte[] dst, byte[] data, int[] lineSize, PixelFormat pf, int x, int y, int c, int w, int read_pal_component)
    //    {
    //        ComponentDescriptor comp = pf.ComponentDescriptions[c];
    //        int plane = comp.Plane;
    //        int depth = comp.DepthMinus1 + 1;
    //        int mask = (1 << depth) - 1;
    //        int step = comp.StepMinus1 + 1;
    //        PixelFormatFlags flags = pf.Flags;

    //        int dstPtr = 0;

    //        if (flags.HasFlag(PixelFormatFlags.BitStream))
    //        {
    //            int skip = x * step + comp.OffsetPlus1 - 1;
    //            int p = data[plane] + y * lineSize[plane] + (skip >> 3);
    //            int shift = 8 - depth - (skip & 7);

    //            while (w-- > 0)
    //            {
    //                int val = (p >> shift) & mask;
    //                if (read_pal_component > 0)
    //                    val = data[1][4 * val + c];
    //                shift -= step;
    //                p -= shift >> 3;
    //                shift &= 7;
    //                dst[dstPtr++] = (byte)val;
    //            }
    //        }
    //        else
    //        {
    //            int p = data[plane] + y * lineSize[plane] + x * step + comp.OffsetPlus1 - 1;

    //            bool is_8bit = comp.Shift + depth <= 8 ? true : false;

    //            if (is_8bit)
    //                p += !!(flags & AV_PIX_FMT_FLAG_BE);

    //            while (w-- > 0)
    //            {
    //                int val = is_8bit ? p : flags & AV_PIX_FMT_FLAG_BE ? AV_RB16(p) : AV_RL16(p);

    //                val = (val >> comp.Shift) & mask;
    //                if (read_pal_component)
    //                    val = data[1][4 * val + c];
    //                p += step;
    //                dst[dstPtr++] = (byte)val;
    //            }
    //        }
    //    }
    //}    
}

namespace Media.UnitTests
{
    internal class ImageFormatTests
    {
        public void TestWithoutAlphaComponent()
        {
            // Arrange
            var originalFormat = ImageFormat.ARGB(8);

            // Act
            var result = ImageFormat.WithoutAlphaComponent(originalFormat);

            // Assert
            System.Diagnostics.Debug.Assert(originalFormat.HasAlphaComponent && !result.HasAlphaComponent);
        }

        public void TestWithProceedingAlphaComponent()
        {
            // Arrange
            var originalFormat = ImageFormat.RGB(8);

            // Act
            var result = ImageFormat.WithProceedingAlphaComponent(originalFormat, 8);

            // Assert
            System.Diagnostics.Debug.Assert(result.HasAlphaComponent);
        }

        public void TestWithPreceedingAlphaComponent()
        {
            // Arrange
            var originalFormat = ImageFormat.RGB(8);

            // Act
            var result = ImageFormat.WithPreceedingAlphaComponent(originalFormat, 8);

            // Assert
            System.Diagnostics.Debug.Assert(result.HasAlphaComponent);
        }

        public void TestBinaryFormat()
        {
            // Act
            var result = ImageFormat.Binary(1);

            // Assert
            System.Diagnostics.Debug.Assert(1 == result.HorizontalSamplingFactors.Length);
            System.Diagnostics.Debug.Assert(1 == result.VerticalSamplingFactors.Length);
        }

        public void TestMonochromeFormat()
        {
            // Act
            var result = ImageFormat.Monochrome(1);

            // Assert
            System.Diagnostics.Debug.Assert(1 == result.HorizontalSamplingFactors.Length);
            System.Diagnostics.Debug.Assert(1 == result.VerticalSamplingFactors.Length);
        }

        public void TestRGBFormat()
        {
            // Act
            var result = ImageFormat.RGB(8);

            // Assert
            System.Diagnostics.Debug.Assert(3 == result.HorizontalSamplingFactors.Length);
            System.Diagnostics.Debug.Assert(3 == result.VerticalSamplingFactors.Length);
        }

        public void TestARGBFormat()
        {
            // Act
            var result = ImageFormat.ARGB(8);

            // Assert
            System.Diagnostics.Debug.Assert(4 == result.HorizontalSamplingFactors.Length);
            System.Diagnostics.Debug.Assert(4 == result.VerticalSamplingFactors.Length);
            System.Diagnostics.Debug.Assert(result.HasAlphaComponent);
        }

        public void TestWithSubSampling()
        {
            // Arrange
            var originalFormat = ImageFormat.YUV(8);
            int[] sampling = { 4, 2, 2 };

            // Act
            var result = ImageFormat.WithSubSampling(originalFormat, sampling);

            // Assert
            System.Diagnostics.Debug.Assert(result.IsSubSampled);
        }

        public void TestPackedFormat()
        {
            // Arrange
            var originalFormat = ImageFormat.YUV(8);

            // Act
            var result = ImageFormat.Packed(originalFormat);

            // Assert
            System.Diagnostics.Debug.Assert(DataLayout.Packed == result.DataLayout);
        }

        public void TestPlanarFormat()
        {
            // Arrange
            var originalFormat = ImageFormat.YUV(8);

            // Act
            var result = ImageFormat.Planar(originalFormat);

            // Assert
            System.Diagnostics.Debug.Assert(DataLayout.Planar == result.DataLayout);
        }
    }
}
