using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HideyNotes.Services
{
    /// <summary>
    /// Printing is separated from the editor on purpose: the editor lives in the dark theme with
    /// light text, while paper is white. The printable document is built from the note text with
    /// black text, the user's font and the printer's page size.
    /// </summary>
    internal static class PrintService
    {
        private const double PageMargin = 48;

        public static void Print(string title, string text, AppSettings settings)
        {
            var dialog = new System.Windows.Controls.PrintDialog();
            if (dialog.ShowDialog() != true) return;

            var document = Build(title, text, settings, dialog.PrintableAreaWidth, dialog.PrintableAreaHeight);

            try
            {
                dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, title);
                Log.Info("printed");
            }
            catch (Exception ex)
            {
                Log.Error("print failed", ex);
            }
        }

        /// <summary>The printable version of a note: plain lines, black on white, no theme colours.</summary>
        public static FlowDocument Build(string title, string text, AppSettings settings, double pageWidth, double pageHeight)
        {
            var family = new FontFamily(string.IsNullOrWhiteSpace(settings.FontFamily) ? "Segoe UI" : settings.FontFamily);

            var document = new FlowDocument
            {
                PageWidth = pageWidth,
                PageHeight = pageHeight,
                PagePadding = new Thickness(PageMargin),
                ColumnWidth = double.PositiveInfinity,
                FontFamily = family,
                FontSize = settings.FontSize,
                LineHeight = Math.Max(1, settings.FontSize * settings.LineHeight),
                Foreground = Brushes.Black,
                Background = Brushes.White
            };

            document.Blocks.Add(new Paragraph(new Run(title))
            {
                FontSize = settings.FontSize + 4,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12)
            });

            // One paragraph per line keeps the note's own line breaks instead of re-flowing the text.
            foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                document.Blocks.Add(new Paragraph(new Run(line))
                {
                    Margin = new Thickness(0, 0, 0, 0)
                });
            }

            return document;
        }

        /// <summary>
        /// Renders the printable document to PNGs, one per page. Used to check the layout without a
        /// printer: HIDEYNOTES_PRINT_PNG=&lt;folder&gt; turns it on, nothing else calls it.
        /// </summary>
        public static void RenderToPng(FlowDocument document, string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var paginator = ((IDocumentPaginatorSource)document).DocumentPaginator;
                paginator.PageSize = new Size(document.PageWidth, document.PageHeight);
                paginator.ComputePageCount();

                for (var i = 0; i < paginator.PageCount; i++)
                {
                    using var page = paginator.GetPage(i);
                    var visual = page.Visual;
                    var width = (int)Math.Ceiling(page.Size.Width);
                    var height = (int)Math.Ceiling(page.Size.Height);

                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    var background = new DrawingVisual();
                    using (var context = background.RenderOpen())
                    {
                        context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                    }

                    bitmap.Render(background);
                    bitmap.Render(visual);

                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(folder, $"page-{i + 1:D2}.png"));
                    encoder.Save(stream);
                }

                Log.Info($"print preview: {paginator.PageCount} page(s)");
            }
            catch (Exception ex)
            {
                Log.Error("print preview failed", ex);
            }
        }
    }
}
