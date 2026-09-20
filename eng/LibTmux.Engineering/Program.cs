using LibTmux.Engineering;
using Microsoft.Build.Locator;

if (args.Length == 3 && args[0] == "api-inventory")
{
    MSBuildLocator.RegisterDefaults();
    await ApiInventory.WriteAsync(args[1], args[2]);
    return 0;
}

if (args.Length == 4 && args[0] == "packages")
{
    MSBuildLocator.RegisterDefaults();
    return PackageInspection.Run(args[1..]);
}

Console.Error.WriteLine("Usage: LibTmux.Engineering api-inventory ROOT OUTPUT | packages ROOT PACKAGES INVENTORY");
return 1;
