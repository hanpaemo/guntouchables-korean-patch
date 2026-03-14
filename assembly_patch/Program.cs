using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: PatchAssemblyCSharp <input-assembly-csharp> <helper-dll> <output-assembly-csharp>");
    Environment.Exit(2);
}

string inputAssemblyPath = Path.GetFullPath(args[0]);
string helperAssemblyPath = Path.GetFullPath(args[1]);
string outputAssemblyPath = Path.GetFullPath(args[2]);

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(inputAssemblyPath)!);
resolver.AddSearchDirectory(Path.GetDirectoryName(helperAssemblyPath)!);

var readerParameters = new ReaderParameters
{
    AssemblyResolver = resolver,
};

AssemblyDefinition targetAssembly = AssemblyDefinition.ReadAssembly(inputAssemblyPath, readerParameters);
AssemblyDefinition helperAssembly = AssemblyDefinition.ReadAssembly(helperAssemblyPath, readerParameters);

TypeDefinition steamManager = targetAssembly.MainModule.Types.FirstOrDefault(t => t.Name == "SteamManager")
    ?? throw new InvalidOperationException("SteamManager type not found.");
TypeDefinition helperType = helperAssembly.MainModule.Types.FirstOrDefault(t => t.FullName == "Hanpaemo.Guntouchables.KoreanFontRuntime")
    ?? throw new InvalidOperationException("KoreanFontRuntime type not found in helper assembly.");
MethodDefinition helperMethod = helperType.Methods.FirstOrDefault(m => m.Name == "RuntimeInitialize" && m.IsStatic && !m.HasParameters)
    ?? throw new InvalidOperationException("RuntimeInitialize method not found in helper assembly.");

string helperAssemblyName = helperAssembly.Name.Name;
if (!targetAssembly.MainModule.AssemblyReferences.Any(r => r.Name == helperAssemblyName))
{
    targetAssembly.MainModule.AssemblyReferences.Add(new AssemblyNameReference(helperAssemblyName, helperAssembly.Name.Version));
}

MethodReference importedHelperMethod = targetAssembly.MainModule.ImportReference(helperMethod);

string[] targetMethodNames = ["Awake", "Start", "OnEnable", "InitOnPlayMode"];
MethodDefinition targetMethod = targetMethodNames
    .Select(methodName => steamManager.Methods.FirstOrDefault(m => m.Name == methodName && m.ReturnType.FullName == "System.Void"))
    .FirstOrDefault(m => m != null && m.HasBody)
    ?? throw new InvalidOperationException("No suitable SteamManager startup method was found.");

bool alreadyPatched = targetMethod.Body.Instructions.Any(i =>
    (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
    i.Operand is MethodReference mr &&
    mr.FullName == importedHelperMethod.FullName);

if (alreadyPatched)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputAssemblyPath)!);
    File.Copy(inputAssemblyPath, outputAssemblyPath, true);
    Console.WriteLine($"Patched Assembly-CSharp method already present: SteamManager.{targetMethod.Name}");
    return;
}

ILProcessor il = targetMethod.Body.GetILProcessor();
Instruction first = targetMethod.Body.Instructions.First();
il.InsertBefore(first, il.Create(OpCodes.Call, importedHelperMethod));

Directory.CreateDirectory(Path.GetDirectoryName(outputAssemblyPath)!);
targetAssembly.Write(outputAssemblyPath, new WriterParameters
{
    WriteSymbols = false,
});

Console.WriteLine($"Patched Assembly-CSharp method: SteamManager.{targetMethod.Name}");
