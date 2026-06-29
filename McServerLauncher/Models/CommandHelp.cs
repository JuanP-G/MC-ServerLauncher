namespace McServerLauncher.Models;

/// <summary>A server command with its syntax and explanation, for the console help.</summary>
/// <param name="Insert">Text placed in the box when chosen (ready to complete the arguments).</param>
/// <param name="Title">The command syntax that is displayed.</param>
/// <param name="Description">What the command does.</param>
public record CommandHelp(string Insert, string Title, string Description);
