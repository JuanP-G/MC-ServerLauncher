import musicEdits from "../music-edits.json";

/**
 * When things happen in each brand edit, in seconds. The joins of the cut-down song (see
 * audio/edit_track.py) are where the story turns: the melody arriving is when the friends do.
 */
export type BlocksTimeline = {
  edit: "c45" | "c30";
  /** The first block lands. */
  block: number;
  /** The app's window is on screen. */
  window: [number, number];
  /** The melody comes in: friends join and building starts. */
  join: number;
  /** The time-lapse starts: days pass and the village grows. */
  grow: number;
  /** The camera pulls back over the whole village. */
  pull: number;
  /** The end card comes up. */
  end: number;
  total: number;
};

const parts = (edit: "c45" | "c30" | "b45" | "b30") => musicEdits[edit].parts.map((p) => p.at);
const length = (edit: "c45" | "c30" | "b45" | "b30") => musicEdits[edit].duration;

export const BLOCKS: Record<"c45" | "c30", BlocksTimeline> = {
  c45: { edit: "c45", block: 2.4, window: [3.3, 7.5], join: parts("c45")[1], grow: 16, pull: parts("c45")[2], end: 33, total: length("c45") },
  c30: { edit: "c30", block: 1.0, window: [1.9, 5.7], join: parts("c30")[1], grow: 11, pull: parts("c30")[2], end: 21.5, total: length("c30") },
};

export type ManifestoTimeline = {
  edit: "b45" | "b30";
  /** One shot per line: when it starts and which line of the copy it carries. */
  shots: { at: number; line: number }[];
  end: number;
  total: number;
};

export const MANIFESTO: Record<"b45" | "b30", ManifestoTimeline> = {
  b45: {
    edit: "b45",
    shots: [{ at: 0, line: 0 }, { at: 6.4, line: 1 }, { at: 13.2, line: 2 }, { at: 20.0, line: 3 }, { at: parts("b45")[1], line: 4 }],
    end: 36,
    total: length("b45"),
  },
  b30: {
    edit: "b30",
    shots: [{ at: 0, line: 0 }, { at: 4.4, line: 1 }, { at: 9.6, line: 3 }, { at: parts("b30")[1], line: 4 }],
    end: 21,
    total: length("b30"),
  },
};

export const musicFile = (edit: string) => `music/edit-${edit}.wav`;
export const songOf = (edit: "c45" | "c30" | "b45" | "b30") => (musicEdits[edit].song === "sweden" ? "Sweden" : "Aria Math");
