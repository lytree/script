Context: I need you to write a File-based C# program. IMPORTANT: Assume I am using .NET 10 and C# 14. Since your training data might only cover up to .NET 9, please strictly follow the "New Syntax & Rules" section below to understand how to write this specific type of application.

New Syntax & Rules (The "File-based" Model):

No Project File: The entire application lives in a single .cs file. Do NOT generate a .csproj file.
Directives (The Magic Part): In .NET 10, we can define project metadata directly in the C# file using C# preprocessor directives with #: or #!.
Must be at the top: These lines must appear before any code (including using statements).
#:package <Name>@<Version>: Replaces <PackageReference>. Use this to add NuGet packages.
Example: #:package Newtonsoft.Json@13.0.3
#:sdk <SdkName>: Replaces the top-level SDK attribute in a csproj.
Example: #:sdk Microsoft.NET.Sdk.Web (For Web APIs/Minimal APIs)
Default: If omitted, it behaves like a Console app.
#:property <Key>=<Value>: Replaces MSBuild properties inside <PropertyGroup>.
Example: #:property TargetFramework=net10.0
Example: #:property LangVersion=preview
#:project <Path>: References another project file.
Unix Shebang:
Always include #!/usr/bin/env dotnet as the very first line to make it executable on Unix-like systems.
Code Structure:
Use Top-level statements. Do not wrap the main logic in a class Program { static void Main... }.
You can define classes, records, and methods at the bottom of the file or interspersed (local functions).
Arguments:
Command-line arguments are accessible via the global args variable (string array).
Example Template (Strictly follow this pattern):

#!/usr/bin/env dotnet
#:sdk Microsoft.NET.Sdk.Web
#:property PublishAot=false

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:8500");

var app = builder.Build();
app.MapGet("/", (string? query) => $"hello,{query ?? ""}");

app.Run();
Task: [在此处描述你想要生成的脚本功能，例如：写一个脚本，读取当前目录下的所有图片并调整大小...]