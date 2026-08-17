namespace Serval.Ai;

/// <summary>A rectangle of a frame, in that frame's own pixels.</summary>
public readonly record struct FrameRegion(int X, int Y, int Width, int Height)
{
    /// <summary>The whole of a frame.</summary>
    public static FrameRegion Whole(int width, int height) => new(0, 0, width, height);
}

/// <summary>
/// Turns a rectangle of a planar yuv420p frame into the exact buffer a detector wants: cropped,
/// scaled to fit, letterboxed, colour-converted and laid out per <see cref="DetectorInput"/>.
///
/// <para>No caller decides any of that: the frame path hands over a region and a detector's declared
/// input, and what comes back is correct for that backend, along with the geometry needed to put a
/// box back where it came from.</para>
///
/// <para>Pure, allocation-free per call, and free of native dependencies, for the same reason the
/// gates are — it sits between every frame and every detector, so it is worth testing exhaustively
/// on a fresh clone with no models fetched.</para>
///
/// <para><b>Sampling is area-averaging.</b> Each destination pixel averages the whole source
/// rectangle it covers rather than point-sampling its centre, which stops a downscale aliasing a
/// small distant object into noise — the same reason the ffmpeg side asks for <c>flags=area</c>.
/// Enlarging, the rectangle is under a pixel and this degenerates to nearest-neighbour.</para>
///
/// <para>Averaging happens in YUV, before conversion, so the colour maths runs once per destination
/// pixel rather than once per source pixel; for an average the two agree to well inside a
/// quantisation step.</para>
/// </summary>
public static class FramePreparer
{
    /// <summary>
    /// The fill for letterbox padding. Mid-grey by convention: it is the least suggestive thing to
    /// show a model, where black reads as shadow and white as blown sky.
    /// </summary>
    private const byte Pad = 127;

    /// <summary>
    /// Prepares one region and reports how to map results back.
    /// </summary>
    /// <param name="frame">Planar yuv420p: a full-resolution luma plane, then half-resolution U and
    /// V planes.</param>
    /// <param name="frameWidth">Frame width in pixels. Must be even.</param>
    /// <param name="frameHeight">Frame height in pixels. Must be even.</param>
    /// <param name="region">The rectangle to prepare. Clamped to the frame.</param>
    /// <param name="input">The detector's declared buffer shape.</param>
    /// <param name="destination">At least <see cref="DetectorInput.ByteLength"/> bytes.</param>
    public static PreparedFrame Prepare(
        ReadOnlySpan<byte> frame,
        int frameWidth,
        int frameHeight,
        FrameRegion region,
        DetectorInput input,
        Span<byte> destination)
    {
        if ((frameWidth & 1) != 0 || (frameHeight & 1) != 0)
        {
            throw new ArgumentException(
                $"yuv420p frames must have even dimensions ({frameWidth}x{frameHeight}); its chroma "
                + "planes are half resolution in both axes and an odd size has no representation.",
                nameof(frameWidth));
        }

        int expected = frameWidth * frameHeight * 3 / 2;
        if (frame.Length < expected)
        {
            throw new ArgumentException(
                $"Frame is {frame.Length} bytes; {frameWidth}x{frameHeight} yuv420p needs {expected}.",
                nameof(frame));
        }

        if (destination.Length < input.ByteLength)
        {
            throw new ArgumentException(
                $"Destination is {destination.Length} bytes; {input.Width}x{input.Height} "
                + $"{input.Layout} needs {input.ByteLength}.",
                nameof(destination));
        }

        FrameRegion crop = Clamp(region, frameWidth, frameHeight);

        // Fit the crop inside the model's input with its aspect ratio intact. Padding is computed
        // here rather than inferred later: an odd difference splits unevenly, and guessing which
        // edge got the extra pixel is exactly the assumption that makes un-mapping wrong by one and
        // impossible to notice.
        float scale = Math.Min(
            (float)input.Width / crop.Width, (float)input.Height / crop.Height);
        int scaledWidth = Math.Clamp((int)MathF.Round(crop.Width * scale), 1, input.Width);
        int scaledHeight = Math.Clamp((int)MathF.Round(crop.Height * scale), 1, input.Height);
        int padX = (input.Width - scaledWidth) / 2;
        int padY = (input.Height - scaledHeight) / 2;

        FillPad(destination, input);

        int lumaSize = frameWidth * frameHeight;
        int chromaWidth = frameWidth / 2;
        int chromaSize = chromaWidth * (frameHeight / 2);
        ReadOnlySpan<byte> luma = frame[..lumaSize];
        ReadOnlySpan<byte> u = frame.Slice(lumaSize, chromaSize);
        ReadOnlySpan<byte> v = frame.Slice(lumaSize + chromaSize, chromaSize);

        for (int destinationY = 0; destinationY < scaledHeight; destinationY++)
        {
            // The source rows this destination row covers. Taking the span between two destination
            // edges rather than a fixed window is what makes this exact at any ratio.
            int top = crop.Y + (destinationY * crop.Height / scaledHeight);
            int bottom = crop.Y + (((destinationY + 1) * crop.Height) / scaledHeight);
            bottom = Math.Max(bottom, top + 1);

            for (int destinationX = 0; destinationX < scaledWidth; destinationX++)
            {
                int left = crop.X + (destinationX * crop.Width / scaledWidth);
                int right = crop.X + (((destinationX + 1) * crop.Width) / scaledWidth);
                right = Math.Max(right, left + 1);

                (byte y, byte cb, byte cr) = Average(
                    luma, u, v, frameWidth, chromaWidth, left, top, right, bottom);

                (byte red, byte green, byte blue) = ToRgb(y, cb, cr);

                Write(
                    destination, input, padX + destinationX, padY + destinationY, red, green, blue);
            }
        }

        return new PreparedFrame(
            scale, padX, padY, crop.X, crop.Y, crop.Width, crop.Height, frameWidth, frameHeight);
    }

    private static FrameRegion Clamp(FrameRegion region, int frameWidth, int frameHeight)
    {
        int x = Math.Clamp(region.X, 0, Math.Max(0, frameWidth - 1));
        int y = Math.Clamp(region.Y, 0, Math.Max(0, frameHeight - 1));

        return new FrameRegion(
            x,
            y,
            Math.Clamp(region.Width, 1, frameWidth - x),
            Math.Clamp(region.Height, 1, frameHeight - y));
    }

    /// <summary>
    /// Mean YUV over a source rectangle.
    ///
    /// Chroma is sampled at its own half resolution rather than being upsampled first: averaging
    /// already-averaged samples over the same area gives the same answer, without materialising a
    /// full-resolution plane that exists only to be collapsed again.
    /// </summary>
    private static (byte Y, byte U, byte V) Average(
        ReadOnlySpan<byte> luma,
        ReadOnlySpan<byte> u,
        ReadOnlySpan<byte> v,
        int frameWidth,
        int chromaWidth,
        int left,
        int top,
        int right,
        int bottom)
    {
        int lumaTotal = 0;
        int lumaCount = 0;

        for (int y = top; y < bottom; y++)
        {
            int row = y * frameWidth;
            for (int x = left; x < right; x++)
            {
                lumaTotal += luma[row + x];
                lumaCount++;
            }
        }

        int chromaTotalU = 0;
        int chromaTotalV = 0;
        int chromaCount = 0;

        int chromaTop = top / 2;
        int chromaBottom = Math.Max(chromaTop + 1, (bottom + 1) / 2);
        int chromaLeft = left / 2;
        int chromaRight = Math.Max(chromaLeft + 1, (right + 1) / 2);
        int chromaHeight = u.Length / chromaWidth;

        for (int y = chromaTop; y < chromaBottom && y < chromaHeight; y++)
        {
            int row = y * chromaWidth;
            for (int x = chromaLeft; x < chromaRight && x < chromaWidth; x++)
            {
                chromaTotalU += u[row + x];
                chromaTotalV += v[row + x];
                chromaCount++;
            }
        }

        return (
            (byte)(lumaTotal / Math.Max(lumaCount, 1)),
            (byte)(chromaCount == 0 ? 128 : chromaTotalU / chromaCount),
            (byte)(chromaCount == 0 ? 128 : chromaTotalV / chromaCount));
    }

    /// <summary>
    /// BT.601 limited range, which is what an IP camera's H.264 carries and what ffmpeg hands over
    /// in <c>yuv420p</c> unless told otherwise.
    ///
    /// Stated as a named conversion rather than inlined because it is a real choice with a visible
    /// consequence: reading limited-range samples as full-range lifts blacks and clips highlights,
    /// which shifts every confidence a detector reports by a small amount in a direction that depends
    /// on the scene. If detections ever disagree with the JPEG path by more than rounding, this is
    /// the first thing to check.
    /// </summary>
    private static (byte R, byte G, byte B) ToRgb(byte y, byte u, byte v)
    {
        int c = y - 16;
        int d = u - 128;
        int e = v - 128;

        return (
            Clamp8(((298 * c) + (409 * e) + 128) >> 8),
            Clamp8(((298 * c) - (100 * d) - (208 * e) + 128) >> 8),
            Clamp8(((298 * c) + (516 * d) + 128) >> 8));

        static byte Clamp8(int value) => (byte)Math.Clamp(value, 0, 255);
    }

    private static void Write(
        Span<byte> destination, DetectorInput input, int x, int y, byte r, byte g, byte b)
    {
        if (input.Layout == DetectorLayout.Uint8Nhwc)
        {
            int offset = ((y * input.Width) + x) * 3;
            destination[offset] = r;
            destination[offset + 1] = g;
            destination[offset + 2] = b;
            return;
        }

        Span<float> floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(destination);
        int plane = input.Width * input.Height;
        int index = (y * input.Width) + x;

        floats[index] = r * input.Scale;
        floats[plane + index] = g * input.Scale;
        floats[(2 * plane) + index] = b * input.Scale;
    }

    /// <summary>
    /// Fills the whole buffer with the padding value, so only the scaled region has to be written
    /// afterwards.
    ///
    /// Cheaper than working out which strips are padding and filling those: the region covers all but
    /// two thin bands in the common case, and a straight fill of a contiguous buffer is the fastest
    /// thing available.
    /// </summary>
    private static void FillPad(Span<byte> destination, DetectorInput input)
    {
        if (input.Layout == DetectorLayout.Uint8Nhwc)
        {
            destination[..input.ByteLength].Fill(Pad);
            return;
        }

        Span<float> floats =
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(destination)
                [..(input.Width * input.Height * 3)];
        floats.Fill(Pad * input.Scale);
    }
}
