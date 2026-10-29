import React from "react";
import { random } from "remotion";
import { F } from "../theme";

/**
 * The set of «Antes de que pite»: a kitchen counter at night with the microwave, the laptop
 * running the app and a phone on a stand, drawn in stage units (the counter is 1920 wide). The
 * wall and the cupboards run well past it on every side, so a tall frame finds set to show too.
 */
export const STAGE = {
  microwave: { x: 140, y: 330, w: 760, h: 440 },
  window: { x: 180, y: 370, w: 470, h: 360 },
  lcd: { x: 700, y: 390, w: 160, h: 64 },
  start: { x: 700, y: 660, w: 160, h: 60 },
  laptop: { x: 980, y: 300, w: 640, h: 360 },
  screen: { x: 1004, y: 318, w: 592, h: 333 },
  phone: { x: 1680, y: 380, w: 210, h: 390 },
  counterY: 770,
};

const C = {
  tile: "#202a35",
  grout: "#18202a",
  wood: "#6b4a2f",
  woodDark: "#4a321f",
  cabinet: "#1b232c",
  steel: "#2c333b",
  steelHi: "#454e58",
  glass: "#0c0f12",
  lcd: "#0a160c",
  digit: "#7cff6b",
  warm: "rgba(255,190,110,1)",
};

/** Seven segments, so the microwave reads like a microwave and not like a web page. */
const SEGMENTS: Record<string, string> = {
  "0": "abcdef", "1": "bc", "2": "abged", "3": "abgcd", "4": "fgbc", "5": "afgcd", "6": "afgedc", "7": "abc", "8": "abcdefg", "9": "abcdfg", "-": "g",
};
export const SevenSeg: React.FC<{ text: string; h: number; color?: string; dim?: string }> = ({ text, h, color = C.digit, dim = "rgba(124,255,107,.08)" }) => {
  const w = h * 0.55;
  const t = h * 0.12;
  const seg = (on: boolean, style: React.CSSProperties, k: string) => (
    <div key={k} style={{ position: "absolute", background: on ? color : dim, borderRadius: t / 2, boxShadow: on ? `0 0 ${t * 1.6}px ${color}` : undefined, ...style }} />
  );
  return (
    <div style={{ display: "flex", gap: h * 0.14, alignItems: "center" }}>
      {[...text].map((ch, i) =>
        ch === ":" ? (
          <div key={i} style={{ display: "flex", flexDirection: "column", gap: h * 0.26 }}>
            {[0, 1].map((k) => <div key={k} style={{ width: t, height: t, borderRadius: t, background: color, boxShadow: `0 0 ${t * 1.6}px ${color}` }} />)}
          </div>
        ) : (
          <div key={i} style={{ position: "relative", width: w, height: h, transform: "skewX(-6deg)" }}>
            {(() => {
              const on = SEGMENTS[ch] ?? "";
              return [
                seg(on.includes("a"), { left: t, right: t, top: 0, height: t }, "a"),
                seg(on.includes("b"), { right: 0, top: t, width: t, height: h / 2 - t * 1.5 }, "b"),
                seg(on.includes("c"), { right: 0, bottom: t, width: t, height: h / 2 - t * 1.5 }, "c"),
                seg(on.includes("d"), { left: t, right: t, bottom: 0, height: t }, "d"),
                seg(on.includes("e"), { left: 0, bottom: t, width: t, height: h / 2 - t * 1.5 }, "e"),
                seg(on.includes("f"), { left: 0, top: t, width: t, height: h / 2 - t * 1.5 }, "f"),
                seg(on.includes("g"), { left: t, right: t, top: h / 2 - t / 2, height: t }, "g"),
              ];
            })()}
          </div>
        ),
      )}
    </div>
  );
};

export type KitchenState = {
  /** What the microwave's display shows. */
  lcd: string;
  /** The microwave is running: light on inside, plate turning. */
  on: boolean;
  /** Seconds since it started, for the turning plate and the popping. */
  run: number;
  /** 0..1 how puffed up the popcorn bag is. */
  puff: number;
  /** Pops happening now: kernels in flight, 0..1 through each. */
  pops: { x: number; p: number }[];
  /** The door, 0 shut to 1 open. */
  door: number;
  /** 0..1 of the start button being pressed. */
  press: number;
  /** What the laptop and the phone show. */
  screen: React.ReactNode;
  phone: React.ReactNode;
};

const Wall: React.FC = () => (
  <>
    <div
      style={{
        position: "absolute",
        left: -2400,
        top: -2400,
        width: 6720,
        height: 2400 + STAGE.counterY,
        background: C.tile,
        backgroundImage: `linear-gradient(${C.grout} 4px, transparent 4px), linear-gradient(90deg, ${C.grout} 4px, transparent 4px)`,
        backgroundSize: "120px 60px",
      }}
    />
    {/* A warm pool of light from a lamp above the counter */}
    <div style={{ position: "absolute", left: -600, top: -900, width: 3200, height: 1900, background: "radial-gradient(ellipse at 50% 70%, rgba(255,190,120,.22), transparent 60%)" }} />
    {/* The counter and the cupboards under it */}
    <div style={{ position: "absolute", left: -2400, top: STAGE.counterY, width: 6720, height: 46, background: `linear-gradient(${C.wood}, ${C.woodDark})`, boxShadow: "0 10px 30px rgba(0,0,0,.6)" }} />
    <div style={{ position: "absolute", left: -2400, top: STAGE.counterY + 46, width: 6720, height: 2400, background: C.cabinet }}>
      {Array.from({ length: 14 }, (_, i) => (
        <div key={i} style={{ position: "absolute", left: i * 480 + 20, top: 30, width: 440, height: 600, border: "3px solid #26313c", borderRadius: 8 }}>
          <div style={{ position: "absolute", right: 30, top: 40, width: 12, height: 90, borderRadius: 6, background: "#5d6873" }} />
        </div>
      ))}
    </div>
  </>
);

const Microwave: React.FC<{ s: KitchenState }> = ({ s }) => {
  const m = STAGE.microwave;
  const w = STAGE.window;
  const lightOn = s.on ? 1 : 0;
  const plate = s.run * 2.2;
  return (
    <>
      <div
        style={{
          position: "absolute",
          left: m.x,
          top: m.y,
          width: m.w,
          height: m.h,
          borderRadius: 26,
          background: `linear-gradient(160deg, ${C.steelHi}, ${C.steel} 45%, #22282f)`,
          boxShadow: "0 30px 60px rgba(0,0,0,.55), inset 0 2px 0 rgba(255,255,255,.12)",
        }}
      />
      {/* Inside: the light, the turning plate, the bag puffing up, kernels popping */}
      <div style={{ position: "absolute", left: w.x, top: w.y, width: w.w, height: w.h, borderRadius: 14, overflow: "hidden", background: C.glass }}>
        <div style={{ position: "absolute", inset: 0, background: `radial-gradient(ellipse at 50% 35%, rgba(255,214,150,${0.85 * lightOn}), rgba(120,70,20,${0.5 * lightOn}) 60%, rgba(0,0,0,.9))` }} />
        <div
          style={{
            position: "absolute",
            left: w.w * 0.12,
            top: w.h * 0.72,
            width: w.w * 0.76,
            height: w.h * 0.16,
            borderRadius: "50%",
            background: `conic-gradient(from ${plate}rad, rgba(255,255,255,.18), rgba(255,255,255,.04), rgba(255,255,255,.18), rgba(255,255,255,.04), rgba(255,255,255,.18))`,
            border: "3px solid rgba(255,255,255,.18)",
          }}
        />
        <div
          style={{
            position: "absolute",
            left: w.w / 2,
            bottom: w.h * 0.2,
            width: 150 + 90 * s.puff,
            height: 70 + 110 * s.puff,
            translate: "-50% 0",
            borderRadius: `${30 + 40 * s.puff}px ${30 + 40 * s.puff}px 12px 12px`,
            background: "linear-gradient(90deg, #e9dcc0, #f6efe0 50%, #d9c9a8)",
            boxShadow: "inset 0 -8px 0 rgba(0,0,0,.08)",
            rotate: `${Math.sin(s.run * 9) * 2 * s.puff}deg`,
          }}
        >
          <div style={{ position: "absolute", left: "18%", right: "18%", top: "30%", height: "22%", background: "#d23b2f", borderRadius: 6, display: "grid", placeItems: "center", color: "#fff", fontFamily: F.display, fontWeight: 900, fontSize: 18 + 10 * s.puff }}>
            POP
          </div>
        </div>
        {s.pops.map((k, i) => (
          <div
            key={i}
            style={{
              position: "absolute",
              left: w.w * k.x,
              top: w.h * (0.55 - Math.sin(Math.PI * k.p) * 0.45),
              width: 26,
              height: 22,
              borderRadius: "45% 55% 50% 50%",
              background: "#fff7e0",
              boxShadow: "0 0 8px rgba(255,240,200,.8)",
              rotate: `${k.p * 540}deg`,
            }}
          />
        ))}
        <div style={{ position: "absolute", inset: 0, background: "linear-gradient(120deg, rgba(255,255,255,.10), transparent 30%, transparent 70%, rgba(255,255,255,.05))" }} />
      </div>
      {/* Steam when it is done */}
      {s.door > 0 &&
        [0, 1, 2, 3].map((i) => (
          <div
            key={i}
            style={{
              position: "absolute",
              left: w.x + w.w * (0.2 + i * 0.2),
              top: w.y - 40 - s.door * 160 - i * 20,
              width: 140,
              height: 140,
              borderRadius: "50%",
              background: "radial-gradient(circle, rgba(255,255,255,.35), transparent 70%)",
              opacity: 1 - s.door * 0.6,
              scale: String(0.6 + s.door),
            }}
          />
        ))}
      {/* Panel: display, keypad, start */}
      <div style={{ position: "absolute", left: STAGE.lcd.x, top: STAGE.lcd.y, width: STAGE.lcd.w, height: STAGE.lcd.h, borderRadius: 8, background: C.lcd, display: "grid", placeItems: "center", boxShadow: "inset 0 2px 8px rgba(0,0,0,.8)" }}>
        <SevenSeg text={s.lcd} h={40} />
      </div>
      <div style={{ position: "absolute", left: STAGE.lcd.x + 10, top: STAGE.lcd.y + 90, display: "grid", gridTemplateColumns: "repeat(3, 40px)", gap: 10 }}>
        {"123456789".split("").map((d) => (
          <div key={d} style={{ height: 30, borderRadius: 6, background: "#1c2128", color: "#9aa5b1", fontFamily: F.mono, fontSize: 16, display: "grid", placeItems: "center" }}>{d}</div>
        ))}
      </div>
      <div
        style={{
          position: "absolute",
          left: STAGE.start.x,
          top: STAGE.start.y + 6 * s.press,
          width: STAGE.start.w,
          height: STAGE.start.h,
          borderRadius: 10,
          background: s.press > 0 ? "#2f8a3a" : "#3ca34a",
          boxShadow: s.press > 0 ? "inset 0 3px 6px rgba(0,0,0,.4)" : "0 6px 0 #1f5e27",
          color: "#fff",
          fontFamily: F.display,
          fontWeight: 800,
          fontSize: 22,
          letterSpacing: ".08em",
          display: "grid",
          placeItems: "center",
        }}
      >
        START
      </div>
    </>
  );
};

const Laptop: React.FC<{ screen: React.ReactNode }> = ({ screen }) => {
  const l = STAGE.laptop;
  const sc = STAGE.screen;
  return (
    <>
      <div style={{ position: "absolute", left: l.x, top: l.y, width: l.w, height: l.h, borderRadius: "18px 18px 6px 6px", background: "#15191e", boxShadow: "0 0 0 2px #2a3038, 0 30px 60px rgba(0,0,0,.5)" }} />
      <div style={{ position: "absolute", left: sc.x, top: sc.y, width: sc.w, height: sc.h, overflow: "hidden", background: "#000" }}>{screen}</div>
      <div style={{ position: "absolute", left: l.x - 40, top: l.y + l.h, width: l.w + 80, height: 22, borderRadius: "0 0 18px 18px", background: "linear-gradient(#3a414a, #22272d)" }} />
    </>
  );
};

const Phone: React.FC<{ content: React.ReactNode }> = ({ content }) => {
  const p = STAGE.phone;
  return (
    <>
      <div style={{ position: "absolute", left: p.x - 10, top: p.y + p.h - 30, width: p.w + 20, height: 46, borderRadius: 6, background: "#3a414a" }} />
      <div style={{ position: "absolute", left: p.x, top: p.y, width: p.w, height: p.h, borderRadius: 22, background: "#07090c", boxShadow: "0 0 0 4px #2a3038, 0 20px 40px rgba(0,0,0,.5)", overflow: "hidden", padding: 10 }}>
        {content}
      </div>
    </>
  );
};

export const Kitchen: React.FC<{ s: KitchenState }> = ({ s }) => (
  <div style={{ position: "absolute", left: 0, top: 0, width: 1920, height: 1080 }}>
    <Wall />
    <Microwave s={s} />
    <Laptop screen={s.screen} />
    <Phone content={s.phone} />
  </div>
);

/** Kernels popping, more and more of them as the bag fills: which ones are in the air at time t. */
export const popsAt = (t: number, from: number, to: number) => {
  const pops: { x: number; p: number }[] = [];
  for (let i = 0; i < 120; i++) {
    const at = from + (to - from) * Math.sqrt(random(`pop${i}`));
    const p = (t - at) / 0.45;
    if (p >= 0 && p <= 1) pops.push({ x: 0.15 + random(`popx${i}`) * 0.7, p });
  }
  return pops;
};
