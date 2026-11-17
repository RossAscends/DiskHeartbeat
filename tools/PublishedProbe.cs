using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
public static class PublishedProbe {
    public static void Run(string publishedDirectory){
        string assemblyPath=System.IO.Path.GetFullPath(System.IO.Path.Combine(publishedDirectory,"DiskHeartbeat.dll"));
        var resolver=new AssemblyDependencyResolver(assemblyPath);
        var context=new AssemblyLoadContext("published-native-verification",true);
        context.Resolving+=(_,name)=>resolver.ResolveAssemblyToPath(name) is string path?context.LoadFromAssemblyPath(path):null;
        try{
            var assembly=context.LoadFromAssemblyPath(assemblyPath);
            var type=assembly.GetType("DiskHeartbeat.NativeDriveReader",true)!;
            var reader=Activator.CreateInstance(type)!;
            var drives=(IEnumerable)type.GetMethod("Enumerate")!.Invoke(reader,null)!;
            Console.WriteLine("Published app native-reader probe: "+assemblyPath);
            foreach(var drive in drives){
                Console.WriteLine(drive);
                var reading=type.GetMethod("Read")!.Invoke(reader,new[]{drive})!;
                Console.WriteLine(JsonSerializer.Serialize(reading,reading.GetType()));
            }
        }finally{context.Unload();}
    }
}
