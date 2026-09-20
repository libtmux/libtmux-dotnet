using LibTmux.Engineering;
using Microsoft.Build.Locator;

if (args.Length == 3 && args[0] == "api-inventory")
{
    MSBuildLocator.RegisterDefaults();
    await ApiInventory.WriteAsync(args[1], args[2]);
    return 0;
}

Console.Error.WriteLine("Usage: LibTmux.Engineering api-inventory ROOT OUTPUT");
return 1;
