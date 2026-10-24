using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig;
using WebApplicationAI.AI.Abstractions;

namespace WebApplicationAI.Services
{
    public class ResumeTextExtractor : IResumeTextExtractor
    {
        private const long MaxBytes = 10 * 1024 * 1024;
        private const int MaxChars = 20_000;

        public async Task<string> ExtractTextAsync(IFormFile file, CancellationToken cancellationToken = default)
        {
            if (file == null || file.Length == 0)
                throw new ArgumentException("Resume file is empty.");

            if (file.Length > MaxBytes)
                throw new ArgumentException("Resume must be smaller than 10 MB.");

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();

            await using var stream = new MemoryStream();
            await file.CopyToAsync(stream, cancellationToken);
            stream.Position = 0;

            string text;

            switch (extension)
            {
                case ".txt":
                    using (var reader = new StreamReader(stream))
                        text = await reader.ReadToEndAsync(cancellationToken);
                    break;

                case ".pdf":
                    var header = new byte[4];
                    if (stream.Read(header, 0, 4) != 4 || System.Text.Encoding.ASCII.GetString(header) != "%PDF")
                        throw new ArgumentException("Uploaded file does not appear to be a valid PDF.");
                    stream.Position = 0;
                    try
                    {
                        using var pdf = PdfDocument.Open(stream);
                        text = string.Join("\n", pdf.GetPages().Select(page => page.Text));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new ArgumentException("Unable to parse PDF.", ex);
                    }
                    break;

                case ".docx":
                    try
                    {
                        using var document = WordprocessingDocument.Open(stream, false);
                        text = document.MainDocumentPart?.Document?.Body?.InnerText ?? string.Empty;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new ArgumentException("Unable to parse DOCX.", ex);
                    }
                    break;

                default:
                    throw new ArgumentException("Only PDF, DOCX and TXT files are supported.");
            }

            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("No readable text found. The resume may be scanned.");

            // Keep within the model's context window so the prompt rules aren't truncated away.
            return text.Length > MaxChars ? text[..MaxChars] : text;
        }
    }
}
