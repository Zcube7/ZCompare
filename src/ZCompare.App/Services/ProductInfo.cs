using System.Reflection;

namespace ZCompare.App.Services;

internal static class ProductInfo
{
    public const string RepositoryUrl = "https://github.com/Zcube7/ZCompare";

    public static Version Version =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    public static string VersionText => Version.ToString(3);
}
