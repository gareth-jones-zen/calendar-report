// calendar-report.exe. Output is Unicode to a console and UTF-8 when redirected (see ConsoleText).
return CalendarReport.Cli.Run(args, new CalendarReport.CliHost { Out = WinBackground.ConsoleText.Out, Err = WinBackground.ConsoleText.Error });
