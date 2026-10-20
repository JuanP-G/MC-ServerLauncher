import { useVideoConfig } from "remotion";
import edits from "./edits.json";

export const BPM = edits.bpm;

export type EditName = keyof typeof edits.edits;
export type Section = { id: string; from: number; beats: number };

/** Frames per beat at the composition's frame rate (12 at 30 fps, 24 at 60). */
export const useBeat = () => {
  const { fps } = useVideoConfig();
  return (fps * 60) / BPM;
};

export const beatFrames = (fps: number) => (fps * 60) / BPM;

/** The sections of an edit with the beat each one starts on. */
export const sectionsOf = (edit: EditName): Section[] => {
  let from = 0;
  return edits.edits[edit].sections.map(([id, beats]) => {
    const s = { id: id as string, from, beats: beats as number };
    from += beats as number;
    return s;
  });
};

export const totalBeats = (edit: EditName) => sectionsOf(edit).reduce((a, s) => a + s.beats, 0);

export const durationOf = (edit: EditName, fps: number) =>
  Math.round(totalBeats(edit) * beatFrames(fps) + edits.edits[edit].tail * fps);

export const sizeOf = (edit: EditName) => ({ width: edits.edits[edit].width, height: edits.edits[edit].height });
