#:package Spectre.Console@*
#:package System.CommandLine@*
using System.CommandLine;
using System.Text;
using Spectre.Console;

var rootCommand = new RootCommand("Description");

var optionVerbose = new Option<bool>("--verbose", "Enable verbose output");
var optionOutput = new Option<string?>("--output", "Output to file");
rootCommand.Options.Add(optionVerbose);
rootCommand.Options.Add(optionOutput);

rootCommand.SetAction((res) =>
{

});

rootCommand.Parse(args);