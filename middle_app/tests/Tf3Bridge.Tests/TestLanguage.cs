using System.Runtime.CompilerServices;

namespace Tf3Bridge.Tests;

internal static class TestLanguage
{
    // Most assertions check the Polish texts; English ones switch with `using var _ = Loc.Use(Lang.En);`.
    [ModuleInitializer]
    internal static void Init() => Loc.Language = Lang.Pl;
}
