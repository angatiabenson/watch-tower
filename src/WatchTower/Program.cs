using WatchTower.Cli;

// Entry point. With no arguments this runs the monitoring host (as a Windows service or a
// console app); see `WatchTower --help` for the install/management commands.
return await WatchTowerCli.RunAsync(args);