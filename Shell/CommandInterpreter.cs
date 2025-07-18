﻿using System.Collections;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;

namespace SubTubular.Shell;

static partial class CommandInterpreter
{
    private const string clearCacheCommand = "clear-cache",
        quoteIdsStartingWithDash = " Note that if the video ID starts with a dash, you have to quote it"
            + @" like ""-1a2b3c4d5e"" or use the entire URL to prevent it from being misinterpreted as a command option.";

    internal static async Task<ExitCode> ParseArgs(string[] args, string originalCommand)
    {
        Task search(SearchCommand cmd) => Program.SearchAsync(cmd, originalCommand);
        Task listKeywords(ListKeywords cmd) => Program.ListKeywordsAsync(cmd, originalCommand);

        RootCommand root = new(AssemblyInfo.Title);

        // see https://learn.microsoft.com/en-us/dotnet/standard/commandline/define-commands#define-subcommands
        root.AddCommand(ConfigureSearch(search));
        root.AddCommand(ConfigureListKeywords(listKeywords));
        root.AddCommand(ConfigureClearCache(Program.ApplyClearCacheAsync));
        root.AddCommand(ConfigureRelease());
        root.AddCommand(ConfigureOpen());
        root.AddCommand(ConfigureRecent(search, listKeywords));

        Parser parser = new CommandLineBuilder(root)
            .UseVersionOption()
            .UseEnvironmentVariableDirective()
            .UseParseDirective()
            .UseSuggestDirective()
            .RegisterWithDotnetSuggest()
            .UseTypoCorrections()
            .UseParseErrorReporting(errorExitCode: (int)ExitCode.ValidationError)
            .CancelOnProcessTermination()

            // see https://learn.microsoft.com/en-us/dotnet/standard/commandline/customize-help
            .UseHelp(ctx => ctx.HelpBuilder.CustomizeLayout(context =>
            {
                var layout = HelpBuilder.Default.GetLayout();

                if (context.Command == root)
                {
                    layout = layout
                        .Skip(1) // Skip the default command description section.
                        .Prepend(_ =>
                        {
                            // enhance heading for branding
                            Console.WriteLine(Program.AsciiHeading + root.Description + " " + AssemblyInfo.InformationalVersion);
                            Console.WriteLine(AssemblyInfo.Copyright);
                        });
                }

                return layout.Append(_ => Console.WriteLine(Environment.NewLine + $"See {AssemblyInfo.RepoUrl} for more info."));
            }))
            .Build();

        var exit = await parser.InvokeAsync(args);

        if (Enum.IsDefined(typeof(ExitCode), exit)) return (ExitCode)exit;
        else return ExitCode.GenericError;
    }

    private static Command ConfigureOpen()
    {
        Command open = new("open", "Opens app-related folders in a file browser.");
        open.AddAlias("o");

        Argument<Folders> folder = new("folder", "The folder to open.");
        open.AddArgument(folder);

        open.SetHandler(folder => ShellCommands.ExploreFolder(Folder.GetPath(folder)), folder);
        return open;
    }
}

internal static partial class BindingExtensions
{
    internal static T Parsed<T>(this InvocationContext context, Argument<T> arg)
        => context.ParseResult.GetValueForArgument(arg);

    internal static T? Parsed<T>(this InvocationContext context, Option<T> option)
    {
        var value = context.ParseResult.GetValueForOption(option);

        // return null instead of an empty collection for enumerable options to make value checks easier
        if (option.AllowMultipleArgumentsPerToken
            && value is IEnumerable enumerable
            && !enumerable.GetEnumerator().MoveNext()) // is empty
            return default;

        return value;
    }
}