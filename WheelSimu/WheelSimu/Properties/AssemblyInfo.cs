using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Android.App;

// 本工程设置了 GenerateAssemblyInfo=false（因为下面手写了 AssemblyVersion 等），
// 副作用是 SDK 不再自动生成 [assembly: SupportedOSPlatform("android23.0")]。
// 缺少该特性时，平台兼容性分析器会认为本程序集"可在所有平台运行"，
// 于是对每一处 Android API 调用都报 CA1416 警告（原本约 700+ 条，掩盖真实问题）。
// 这里手工补回，与 csproj 的 <SupportedOSPlatformVersion>23</SupportedOSPlatformVersion> 一致。
[assembly: SupportedOSPlatform("android23.0")]

// General Information about an assembly is controlled through the following 
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("WheelSimu")]
[assembly: AssemblyDescription("")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("WheelSimu")]
[assembly: AssemblyCopyright("Copyright ©  2020")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
[assembly: ComVisible(false)]

// Version information for an assembly consists of the following four values:
//
//      Major Version
//      Minor Version 
//      Build Number
//      Revision
//
// You can specify all the values or you can default the Build and Revision Numbers 
// by using the '*' as shown below:
// [assembly: AssemblyVersion("1.0.*")]
[assembly: AssemblyVersion("1.0.0.1")]
[assembly: AssemblyFileVersion("1.0.0.1")]
#if DEBUG
[assembly: Application(Debuggable = true)]
#else
[assembly: Application(Debuggable = false)]
#endif
[assembly: UsesPermission(Android.Manifest.Permission.AccessNetworkState)]