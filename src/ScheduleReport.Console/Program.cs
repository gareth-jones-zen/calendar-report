// schedule-report.exe. Output is Unicode to a console and UTF-8 when redirected (see ConsoleText).
return ScheduleReport.Cli.Run(args, new ScheduleReport.CliHost { Out = WinBackground.ConsoleText.Out, Err = WinBackground.ConsoleText.Error });
