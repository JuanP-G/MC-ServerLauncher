namespace McServerLauncher.Models;

/// <summary>Un comando de servidor con su sintaxis y explicación, para la ayuda de la consola.</summary>
/// <param name="Insert">Texto que se pone en la caja al elegirlo (listo para completar argumentos).</param>
/// <param name="Title">Sintaxis del comando que se muestra.</param>
/// <param name="Description">Qué hace el comando.</param>
public record CommandHelp(string Insert, string Title, string Description);
