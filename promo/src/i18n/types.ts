// Everything the video says, in one place per language. A new language is a copy of en.ts with
// the text translated, plus its entry in index.ts; see README.md for recording its footage.

export type Feature = {
  /** The big line, in the game's letters. Short: it has a beat or two to be read. */
  title: string;
  /** One plain sentence under it. */
  sub: string;
};

export type Copy = {
  /** BCP 47 tag, used for number formatting. */
  locale: string;
  chaos: {
    question: string;
    /** The made-up error windows of the "before", one per cut. */
    windows: { title: string; lines: string[] }[];
    /** Words stamped over the windows, one per cut. */
    stamps: string[];
    giveUp: string;
  };
  click: { now: string; oneClick: string; button: string };
  logo: { tagline: string };
  features: {
    create: Feature;
    mods: Feature;
    internet: Feature;
    bedrock: Feature;
    players: Feature;
    backups: Feature;
    sleep: Feature;
    console: Feature;
  };
  sleep: { motdSleeping: string; motdStarting: string; serverName: string };
  dual: { noob: string; noobItems: string[]; pro: string; proItems: string[] };
  community: {
    friends: string;
    community: string;
    serverName: string;
    motd: string;
    /** More servers filling the game's list. */
    others: { name: string; motd: string; players: string }[];
  };
  wall: string[];
  /** The two slower brand videos. */
  brand: {
    /** «Bloque a bloque»: the captions, in the order they appear. */
    blocks: { first: string; server: string; friends: string; grow: string; community: string };
    /** «Manifiesto»: one line per shot. *Words between asterisks* are set in bold. */
    manifesto: string[];
    /** The line under the logo at the end of both. */
    tagline: string;
    /** "Music" in the credit line. */
    music: string;
  };
  /** The spot «Antes de que pite». */
  spot: {
    clock: string;
    /** The name of the friends' group chat. */
    group: string;
    twoMinutes: string;
    you: string;
    chat: { who?: string; text: string }[];
    already: string;
    ding: string;
    gameChat: string[];
    tagline: string;
  };
  outro: {
    tagline: string;
    free: string;
    platforms: string;
    download: string;
    url: string;
    disclaimer: string;
  };
};
