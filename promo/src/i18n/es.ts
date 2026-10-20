import type { Copy } from "./types";

export const es: Copy = {
  locale: "es-ES",
  chaos: {
    question: "¿Montar un servidor de Minecraft?",
    windows: [
      { title: "cmd.exe", lines: ["C:\\server> java -jar server.jar", "Error: Unable to access jarfile server.jar"] },
      { title: "eula.txt", lines: ["#By changing the setting below to TRUE...", "eula=false"] },
      { title: "run.bat", lines: ["@echo off", "java -Xmx2G -jar server.jar nogui", "pause"] },
      { title: "cmd.exe", lines: ["[WARN] **** FAILED TO BIND TO PORT!", "Perhaps a server is already running on that port?"] },
      { title: "Router · 192.168.1.1", lines: ["Reenvío de puertos", "Puerto externo: 25565   Protocolo: TCP/UDP", "Contraseña incorrecta"] },
      { title: "server.properties", lines: ["online-mode=true", "white-list=???", "server-ip=", "max-players=20"] },
      { title: "cmd.exe", lines: ["'java' no se reconoce como un comando", "interno o externo."] },
      { title: "cmd.exe", lines: ["Exception in thread \"main\"", "java.lang.UnsupportedClassVersionError"] },
    ],
    stamps: [".BAT", "EULA", "JAVA", "PUERTOS", "ROUTER", "ERROR", "¿IP?", "¿MODS?"],
    giveUp: "…y al final, nadie juega.",
  },
  click: { now: "Ahora,", oneClick: "un clic.", button: "Crear servidor" },
  logo: { tagline: "Tu servidor de Minecraft, sin complicaciones." },
  features: {
    create: { title: "Tu servidor en 2 minutos", sub: "Vanilla, Paper, Purpur, Fabric, Forge o NeoForge. Y trae Java." },
    mods: { title: "Mods y plugins con un clic", sub: "Modrinth dentro de la app, ya filtrado por tu versión." },
    internet: { title: "Juega con amigos online", sub: "Sin abrir puertos: una dirección y a jugar." },
    bedrock: { title: "También desde Bedrock", sub: "Móvil, consola y Windows con un interruptor." },
    players: { title: "Cada jugador, su historia", sub: "Operadores, baneos, lista blanca y estadísticas." },
    backups: { title: "Tu mundo, a salvo", sub: "Copias al arrancar, al parar y cada hora." },
    sleep: { title: "Duerme si no hay nadie", sub: "Y se enciende solo cuando alguien entra." },
    console: { title: "Una consola que se entiende", sub: "Colores por tipo, filtros contados y búsqueda." },
  },
  sleep: {
    motdSleeping: "Apagado · entra para encenderlo",
    motdStarting: "Arrancando… vuelve en unos segundos",
    serverName: "Supervivencia",
  },
  dual: {
    noob: "¿Primera vez?",
    noobItems: ["Sin comandos", "Java incluido", "Sin abrir puertos"],
    pro: "¿Veterano?",
    proItems: ["consola filtrada", "túneles · Bedrock · ViaVersion", "copias · hibernar · SHA-256"],
  },
  community: {
    friends: "Para tus amigos.",
    community: "Para tu comunidad.",
    serverName: "Supervivencia",
    motd: "Temporada 3 · ¡El nether ya tiene estación!",
    others: [
      { name: "Skyblock", motd: "Islas · economía · eventos", players: "96/120" },
      { name: "Creativo", motd: "Parcelas de 128×128 · WorldEdit", players: "64/100" },
      { name: "Minijuegos", motd: "BedWars · SkyWars · Parkour", players: "211/250" },
    ],
  },
  wall: ["Windows", "Linux", "macOS", "5 idiomas", "Se actualiza solo", "SHA-256", "Código abierto", "Gratis"],
  outro: {
    tagline: "Tu servidor de Minecraft, sin complicaciones.",
    free: "Gratis y de código abierto",
    platforms: "Windows · Linux · macOS",
    download: "Descargar gratis",
    url: "mc-server-launcher.vercel.app",
    disclaimer: "No es un producto oficial de Minecraft. No está aprobado ni asociado con Mojang o Microsoft.",
  },
};
