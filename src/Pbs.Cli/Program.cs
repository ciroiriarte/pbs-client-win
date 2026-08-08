using System.Reflection;

// Minimal command surface stub for M0. Real verbs (backup, restore, snapshot, prune) are added
// as the transport core lands in M1+.
var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
{
    Console.WriteLine("pbs-client-win — Proxmox Backup client for Windows");
    Console.WriteLine($"version {version}");
    Console.WriteLine();
    Console.WriteLine("Usage: pbs-client-win <command> [options]");
    Console.WriteLine();
    Console.WriteLine("Commands (planned):");
    Console.WriteLine("  backup    Create a backup snapshot on a PBS datastore");
    Console.WriteLine("  restore   Restore an archive from a snapshot");
    Console.WriteLine("  snapshot  List/inspect snapshots");
    Console.WriteLine("  prune     Apply retention to a datastore");
    return 0;
}

if (args[0] is "version" or "--version")
{
    Console.WriteLine(version);
    return 0;
}

Console.Error.WriteLine($"unknown command: {args[0]}");
Console.Error.WriteLine("run 'pbs-client-win --help' for usage");
return 1;
