namespace Jellyfin.Plugin.MetaTagger.Configuration;

public sealed class PluginConfigurationValidationResult
{
    public PluginConfigurationValidationResult(
        IReadOnlyCollection<string> errors,
        string effectiveGeneratedNamespace,
        string effectiveManualNamespace,
        string effectiveSeparator)
    {
        Errors = errors;
        EffectiveGeneratedNamespace = effectiveGeneratedNamespace;
        EffectiveManualNamespace = effectiveManualNamespace;
        EffectiveSeparator = effectiveSeparator;
    }

    public bool IsValid => Errors.Count == 0;

    public IReadOnlyCollection<string> Errors { get; }

    public string EffectiveGeneratedNamespace { get; }

    public string EffectiveManualNamespace { get; }

    public string EffectiveSeparator { get; }
}
