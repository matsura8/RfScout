namespace RfScout.Core.Display;

/// <summary>
/// The scrolling waterfall's data model: a band of colour rows written one sweep at a time.
///
/// A waterfall is a spectrum turned on its side and scrolled, so the only real decisions are which
/// dB values map to which colour and how the rows move. Both belong in Core: the row writer works
/// on a plain BGRA byte span, so it can be tested without a WriteableBitmap, and the control that
/// paints it does nothing but copy the rows the model produced.
///
/// Rows live in a circular buffer and the caller is told which row is now the newest, so scrolling
/// costs one integer instead of copying the whole bitmap every refresh - which at a 24000-bin span
/// and 180 rows would be 17 MB of copying ten times a second.
/// </summary>
public sealed class WaterfallModel
{
    /// <summary>Rows kept. Deep enough to be a useful history, shallow enough to redraw cheaply.</summary>
    public const int DefaultRows = 180;

    private readonly Lock _gate = new();
    private byte[] _pixels = [];
    private int _columns;
    private int _rows = DefaultRows;
    private int _bytesPerRow;
    private int _nextRow;
    private long _writes;

    /// <summary>Columns in a row, i.e. how many bins the strip covers.</summary>
    public int Columns
    {
        get
        {
            lock (_gate)
            {
                return _columns;
            }
        }
    }

    /// <summary>Rows of history held.</summary>
    public int Rows
    {
        get
        {
            lock (_gate)
            {
                return _rows;
            }
        }
    }

    /// <summary>Strips written since the grid changed.</summary>
    public long Writes
    {
        get
        {
            lock (_gate)
            {
                return Interlocked.Read(ref _writes);
            }
        }
    }

    /// <summary>Row index that holds the newest sweep. Draw it at the top.</summary>
    public int NewestRow
    {
        get
        {
            lock (_gate)
            {
                return (_nextRow - 1 + _rows) % _rows;
            }
        }
    }

    /// <summary>Bytes in one row: <see cref="Columns"/> BGRA pixels.</summary>
    public int BytesPerRow
    {
        get
        {
            lock (_gate)
            {
                return _bytesPerRow;
            }
        }
    }

    /// <summary>
    /// Make room for a <paramref name="columns"/> by <paramref name="rows"/> strip. Any history is
    /// dropped, because a new span or bin width has nothing in common with the old one.
    /// </summary>
    public void Resize(int columns, int rows = DefaultRows)
    {
        if (columns <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(columns), columns, "A strip needs at least one column.");
        }

        if (rows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rows), rows, "A waterfall needs at least one row.");
        }

        lock (_gate)
        {
            if (_columns == columns && _rows == rows)
            {
                return;
            }

            _columns = columns;
            _rows = rows;
            _bytesPerRow = columns * 4;
            _pixels = new byte[_bytesPerRow * rows];
            _nextRow = 0;
            Interlocked.Exchange(ref _writes, 0);
        }
    }

    /// <summary>
    /// Write one sweep into the next row. The bins are resampled to <see cref="Columns"/> so a
    /// 24000-bin span still fits a 1200-pixel strip, and each column takes the strongest bin under
    /// it, which keeps a narrow carrier visible instead of averaging it into the floor.
    /// </summary>
    /// <returns>The row index holding this sweep, or -1 when nothing was written.</returns>
    public int WriteRow(ReadOnlySpan<float> powerDb, DbRange range, ColorMapKind kind = ColorMapKind.Viridis)
    {
        ArgumentNullException.ThrowIfNull(range);

        lock (_gate)
        {
            if (_columns == 0 || powerDb.IsEmpty)
            {
                return -1;
            }

            int row = _nextRow;
            _nextRow = (row + 1) % _rows;
            Span<byte> target = _pixels.AsSpan(row * _bytesPerRow, _bytesPerRow);

            FillRow(target, powerDb, range, kind, _columns);

            Interlocked.Increment(ref _writes);
            return row;
        }
    }

    /// <summary>Copy one row out of the strip so the control can blit it into its bitmap.</summary>
    public bool TryCopyRow(int row, Span<byte> destination)
    {
        lock (_gate)
        {
            if (row < 0 || row >= _rows || destination.Length < _bytesPerRow)
            {
                return false;
            }

            _pixels.AsSpan(row * _bytesPerRow, _bytesPerRow).CopyTo(destination);
            return true;
        }
    }

    /// <summary>The whole strip as bytes, row 0 first. Used by tests and by a save-to-file path.</summary>
    public byte[] Snapshot()
    {
        lock (_gate)
        {
            return (byte[])_pixels.Clone();
        }
    }

    /// <summary>Clear the strip (call when the plan or the colour range changes).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _pixels.AsSpan().Clear();
            _nextRow = 0;
            Interlocked.Exchange(ref _writes, 0);
        }
    }

    /// <summary>
    /// Paint one row into a BGRA span. Public so a test can check the colour mapping and the
    /// resampling without a bitmap, and so the control can paint straight into a WriteableBitmap
    /// back buffer.
    /// </summary>
    public static void FillRow(
        Span<byte> rowBgra,
        ReadOnlySpan<float> powerDb,
        DbRange range,
        ColorMapKind kind,
        int columns)
    {
        ArgumentNullException.ThrowIfNull(range);

        if (rowBgra.Length < (columns * 4) || columns <= 0 || powerDb.IsEmpty)
        {
            return;
        }

        // Empty bins get the bottom of the ramp rather than being skipped: a hole in the waterfall
        // reads as a dead band, and a row is a history line, not a value to interpolate.
        double binCount = powerDb.Length;
        for (int column = 0; column < columns; column++)
        {
            int first = (int)((column * binCount) / columns);
            int last = (int)(((column + 1) * binCount) / columns);
            if (first >= powerDb.Length)
            {
                first = powerDb.Length - 1;
            }

            if (last <= first)
            {
                last = first + 1;
            }

            if (last > powerDb.Length)
            {
                last = powerDb.Length;
            }

            float best = float.MinValue;
            for (int i = first; i < last; i++)
            {
                float value = powerDb[i];
                // Non-finite bins (NaN, or +/-Infinity from a log of zero or an overflow) carry
                // no level, so they count as empty rather than painting the top of the ramp.
                if (value != SpectrumFrame.EmptyBin && float.IsFinite(value) && value > best)
                {
                    best = value;
                }
            }

            double db = best == float.MinValue ? range.MinDb : best;
            ColorMap.WritePixel(rowBgra[(column * 4)..], range.Normalize(db), kind);
        }
    }
}
