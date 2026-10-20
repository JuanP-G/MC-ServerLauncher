import { Audio } from "@remotion/media";
import React from "react";
import { AbsoluteFill, Sequence, staticFile, useVideoConfig } from "remotion";
import { BlockWipe } from "./components/BlockWipe";
import { Sfx } from "./components/Sfx";
import { LANGUAGES } from "./i18n";
import { Chaos } from "./scenes/Chaos";
import { Click } from "./scenes/Click";
import { Community } from "./scenes/Community";
import { Dual } from "./scenes/Dual";
import { Feature } from "./scenes/Feature";
import { Logo } from "./scenes/Logo";
import { Outro } from "./scenes/Outro";
import { Wall } from "./scenes/Wall";
import type { FeatureKey } from "./shots";
import { beatFrames, sectionsOf, type EditName } from "./timing";

export type EditProps = { edit: EditName; lang: string };

/** Sections that cut to the next one with falling blocks rather than straight. */
const WIPED = (id: string) => id.startsWith("f-") || ["dual", "community", "wall"].includes(id);

/** One edit (45 s, 30 s or 15 s) in one language: the sections of edits.json, one after another. */
export const Edit: React.FC<EditProps> = ({ edit, lang }) => {
  const { fps } = useVideoConfig();
  const B = beatFrames(fps);
  const copy = LANGUAGES[lang];
  const sections = sectionsOf(edit);
  let featureIndex = 0;
  return (
    <AbsoluteFill style={{ background: "#000" }}>
      {sections.map((s) => {
        const from = Math.round(s.from * B);
        const duration = Math.round(s.beats * B);
        let scene: React.ReactNode = null;
        if (s.id === "chaos") scene = <Chaos copy={copy} beats={s.beats} />;
        else if (s.id === "click") scene = <Click copy={copy} beats={s.beats} />;
        else if (s.id === "logo") scene = <Logo copy={copy} lang={lang} beats={s.beats} />;
        else if (s.id.startsWith("f-"))
          scene = <Feature copy={copy} lang={lang} feature={s.id.slice(2) as FeatureKey} beats={s.beats} index={featureIndex++} />;
        else if (s.id === "dual") scene = <Dual copy={copy} beats={s.beats} />;
        else if (s.id === "community") scene = <Community copy={copy} beats={s.beats} />;
        else if (s.id === "wall") scene = <Wall copy={copy} beats={s.beats} />;
        else if (s.id === "outro") scene = <Outro copy={copy} beats={s.beats} />;
        // The last section holds through the tail, so the end card is the final frame.
        const last = s === sections[sections.length - 1];
        return (
          <Sequence key={s.id} name={s.id} from={from} durationInFrames={last ? undefined : duration} premountFor={fps}>
            {scene}
          </Sequence>
        );
      })}
      {sections.map((s, i) => {
        const next = sections[i + 1];
        if (!next || !WIPED(next.id) || s.id === "logo") return null;
        const at = Math.round(next.from * B);
        return (
          <React.Fragment key={`w${i}`}>
            <BlockWipe at={at} half={Math.round(B / 2.4)} seed={next.id} />
            <Sfx at={at - Math.round(B / 2.4)} name="whoosh" volume={0.45} />
          </React.Fragment>
        );
      })}
      <Audio src={staticFile(`audio/music-${edit}.wav`)} />
    </AbsoluteFill>
  );
};
