namespace RfScout.Core.Display;

/// <summary>
/// A peak marker the spectrum and waterfall plots draw: where the peak sits, how strong it is,
/// and whether the operator has it selected (the selected marker is highlighted, and the table
/// row with the same <see cref="Id"/> is the one that lights up, which is how two-way selection
/// between table and markers works without either side owning the other).
/// </summary>
/// <param name="Id">Stable tracker ID shared with the peak table row.</param>
/// <param name="CenterHz">Peak centre frequency in absolute Hz.</param>
/// <param name="PowerDb">Peak power in dBFS, used to size or shade the marker.</param>
/// <param name="Selected">True for the peak the operator selected.</param>
public readonly record struct PeakMarker(int Id, double CenterHz, double PowerDb, bool Selected);
