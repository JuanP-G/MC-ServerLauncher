import { Audio } from "@remotion/media";
import React from "react";
import { Sequence, staticFile } from "remotion";

export type SfxName = "click" | "bigclick" | "whoosh" | "block" | "pop" | "key" | "error" | "glitch" | "ding" | "microwave";

/** One sound effect at a frame of the current sequence. */
export const Sfx: React.FC<{ at: number; name: SfxName; volume?: number }> = ({ at, name, volume = 1 }) => (
  <Sequence from={Math.round(at)} durationInFrames={45} layout="none" name={`sfx ${name}`}>
    <Audio src={staticFile(`audio/sfx/${name}.wav`)} volume={volume} />
  </Sequence>
);
