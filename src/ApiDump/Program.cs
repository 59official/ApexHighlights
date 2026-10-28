using System;using System.Linq;using System.Reflection;using Vortice.MediaFoundation;
class P{
 static void Dump(Type? t,string name){
  if(t==null){Console.WriteLine($"### {name}: NOT FOUND");return;}
  Console.WriteLine($"### {name}");
  foreach(var m in t.GetMembers(BindingFlags.Public|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly))
   Console.WriteLine("   "+m.MemberType+" "+m);
 }
 static void Main(){
  var asm=typeof(IMFTransform).Assembly;
  Dump(typeof(MediaFactory),"MediaFactory[MFStartup/MFCreate*]");
  Console.WriteLine(string.Join("\n", typeof(MediaFactory).GetMethods().Where(x=>x.Name.Contains("Startup")||x.Name.Contains("MFCreate")||x.Name.Contains("MFTEnum")).Select(x=>x.ToString())));
  Dump(asm.GetType("Vortice.MediaFoundation.RegisterTypeInfo"),"RegisterTypeInfo");
  Dump(asm.GetType("Vortice.MediaFoundation.OutputStreamInfo"),"OutputStreamInfo");
  Dump(asm.GetType("Vortice.MediaFoundation.TMessageType"),"TMessageType");
  Console.WriteLine("--- enum values TMessageType: "+string.Join(",",Enum.GetNames(asm.GetType("Vortice.MediaFoundation.TMessageType")!)));
  Dump(typeof(IMFActivate),"IMFActivate");
  Dump(typeof(IMFSample),"IMFSample");
  Dump(typeof(IMFTransform),"IMFTransform(ProcessOutput)");
  foreach(var m in typeof(IMFTransform).GetMethods().Where(x=>x.Name.StartsWith("Process")||x.Name=="SetInputType"||x.Name=="SetOutputType")) Console.WriteLine("   "+m);
  foreach(var m in typeof(IMFAttributes).GetMethods().Where(x=>x.Name=="Set")) Console.WriteLine("   Set: "+m);
  foreach(var m in typeof(IMFMediaBuffer).GetMethods()) Console.WriteLine("   MB: "+m);
 }
}
