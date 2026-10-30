using System.Threading;
using System.Threading.Tasks;

namespace Tools.Services.Translation;

public interface ITranslationEngine
{
    string Name { get; }
    Task<string> TranslateAsync(string text, string sourceLang, string targetLang, CancellationToken cancellationToken = default);
}
