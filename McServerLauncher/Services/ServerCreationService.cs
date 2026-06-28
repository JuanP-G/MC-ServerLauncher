using System.IO;

namespace McServerLauncher.Services;

/// <summary>
/// Genera los archivos iniciales de un servidor nuevo: eula.txt, run.bat y un
/// server.properties mínimo con el puerto elegido (Minecraft completará el resto
/// en el primer arranque).
/// </summary>
public class ServerCreationService
{
    /// <summary>Acepta el EULA de Minecraft escribiendo eula.txt.</summary>
    public void WriteEula(string folder)
    {
        File.WriteAllText(Path.Combine(folder, "eula.txt"),
            "# Generado por MC Server Launcher\r\neula=true\r\n");
    }

    /// <summary>
    /// Crea run.bat equivalente al que usarías manualmente (por si quieres lanzarlo fuera de la app).
    /// </summary>
    public void WriteRunBat(string folder, int minGb, int maxGb, string jarFile, string javaPath = "java")
    {
        var java = javaPath.Contains(' ') ? $"\"{javaPath}\"" : javaPath;
        var content = $"@echo off\r\n{java} -Xms{minGb}G -Xmx{maxGb}G -jar \"{jarFile}\" nogui\r\npause\r\n";
        File.WriteAllText(Path.Combine(folder, "run.bat"), content);
    }

    /// <summary>
    /// Escribe un server.properties mínimo con el puerto y el MOTD si aún no existe.
    /// Minecraft rellenará el resto de propiedades por defecto al arrancar.
    /// </summary>
    public void WriteInitialProperties(string folder, int port, string motd)
    {
        var path = Path.Combine(folder, "server.properties");
        if (File.Exists(path))
            return;

        var content = $"#Minecraft server properties\r\nserver-port={port}\r\nmotd={motd}\r\n";
        File.WriteAllText(path, content);
    }
}
