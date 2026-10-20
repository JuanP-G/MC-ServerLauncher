import React from "react";
import { Composition, Folder } from "remotion";
import { Edit, type EditProps } from "./Edit";
import { LANGUAGES } from "./i18n";
import { durationOf, sizeOf, type EditName } from "./timing";
import "./theme";

const FPS = 30;
const EDITS: EditName[] = ["promo45", "vertical30", "short15"];

/** Every edit in every language: promo45-es, vertical30-en, and so on. */
export const Root: React.FC = () => (
  <>
    {Object.keys(LANGUAGES).map((lang) => (
      <Folder key={lang} name={lang}>
        {EDITS.map((edit) => (
          <Composition
            key={edit}
            id={`${edit}-${lang}`}
            component={Edit as unknown as React.FC<Record<string, unknown>>}
            durationInFrames={durationOf(edit, FPS)}
            fps={FPS}
            {...sizeOf(edit)}
            defaultProps={{ edit, lang } satisfies EditProps}
          />
        ))}
      </Folder>
    ))}
  </>
);
