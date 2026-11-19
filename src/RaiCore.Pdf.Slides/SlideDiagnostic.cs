namespace RaiCore.Pdf.Slides;

/// <summary>
/// Something in a presentation that the converter did not draw as a presentation program would (see
/// <see cref="PptxConverterOptions.Diagnostics"/>).
/// </summary>
/// <param name="SlideNumber">The number of the slide it concerns, from 1 in presentation order; 0 for the presentation as a whole.</param>
/// <param name="Subject">What it concerns, such as <c>chart</c>, <c>picture</c> or <c>SmartArt</c>.</param>
/// <param name="Message">What was not drawn, or drawn differently, and why.</param>
public sealed record SlideDiagnostic(int SlideNumber, string Subject, string Message);
