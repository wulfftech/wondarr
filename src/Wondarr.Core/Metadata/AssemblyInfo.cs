using System.Runtime.CompilerServices;

// MetadataOptions.RetryBaseDelay is internal because it exists for tests only: production always
// backs off for the polite two seconds. The composition test lives in the sources test project.
[assembly: InternalsVisibleTo("Wondarr.Sources.Tests")]
[assembly: InternalsVisibleTo("Wondarr.Core.Tests")]
