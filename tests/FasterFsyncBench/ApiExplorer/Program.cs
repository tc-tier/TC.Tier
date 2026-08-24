using System.Reflection;

var raftAsm = Assembly.LoadFile(
    Path.GetFullPath("/home/tang/.nuget/packages/dotnext.net.cluster/5.26.2/lib/net8.0/DotNext.Net.Cluster.dll"));

Console.WriteLine("=== IRaftLogEntry inheritance ===");
var irt = raftAsm.GetType("DotNext.Net.Cluster.Consensus.Raft.IRaftLogEntry");
var t = irt!;
while (t != null)
{
    Console.WriteLine(t.FullName);
    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        Console.WriteLine($"  prop: {p.PropertyType.Name} {p.Name}");
    foreach (var i in t.GetInterfaces())
        Console.WriteLine($"  : {i.FullName}");
    t = t.BaseType;
    if (t == typeof(object) || t == null) break;
    Console.WriteLine("  inherits from:");
}

Console.WriteLine();
Console.WriteLine("=== ILogEntry inheritance ===");
var ile = raftAsm.GetType("DotNext.Net.Cluster.Consensus.Raft.ILogEntry");
if (ile == null)
    ile = AppDomain.CurrentDomain.GetAssemblies()
        .SelectMany(a => a.GetTypes())
        .FirstOrDefault(t => t.Name == "ILogEntry" && t.Namespace?.Contains("Raft") == true);
if (ile != null)
{
    Console.WriteLine(ile.FullName);
    foreach (var p in ile.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        Console.WriteLine($"  prop: {p.PropertyType.Name} {p.Name}");
    foreach (var m in ile.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        Console.WriteLine($"  method: {m.ReturnType.Name} {m.Name}");
}

Console.WriteLine();
Console.WriteLine("=== DotNext.IO.Log.ILogEntry ===");
var ioAsm = Assembly.LoadFile(Path.GetFullPath("/home/tang/.nuget/packages/dotnext.io/5.26.2/lib/net8.0/DotNext.IO.dll"));
foreach (var t2 in ioAsm.GetTypes().Where(t => t.Name == "ILogEntry"))
{
    Console.WriteLine(t2.FullName);
    foreach (var p in t2.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        Console.WriteLine($"  prop: {p.PropertyType.Name} {p.Name}");
}
