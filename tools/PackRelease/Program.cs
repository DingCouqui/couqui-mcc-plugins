using DMCBK.Marketplace;

if (args.Length != 6)
{
    Console.Error.WriteLine("Usage: <author> <payload> <output> <kind> <target> <asset-base-url>");
    return 2;
}

PackedPlugin packed = PluginPackageBuilder.Pack(
    Path.GetFullPath(args[0]),
    Path.GetFullPath(args[1]),
    Path.GetFullPath(args[2]),
    args[3],
    args[4],
    new Uri(args[5]));

Console.WriteLine(packed.ArchivePath);
Console.WriteLine(packed.Release.Assets[0].Sha256);
return 0;
