// Turns each recording's manifest.tsv into src/footage.json: frame files, how long each one stays
// on screen and where the cursor was. Run after recording: node scripts/index-footage.mjs
import fs from "node:fs";
import path from "node:path";

const root = path.resolve(import.meta.dirname, "..");
const dir = path.join(root, "public", "footage");
const index = {};
for (const name of fs.readdirSync(dir)) {
  const manifest = path.join(dir, name, "manifest.tsv");
  if (!fs.existsSync(manifest)) continue;
  let t = 0;
  const frames = fs.readFileSync(manifest, "utf8").trim().split(/\r?\n/).map((line) => {
    const [file, x, y, ms, click] = line.split("\t");
    const f = { file, at: t, x: Number(x), y: Number(y), click: Number(click) };
    t += Number(ms);
    return f;
  });
  index[name] = { duration: t, frames };
}
fs.writeFileSync(path.join(root, "src", "footage.json"), JSON.stringify(index));
console.log(Object.keys(index).sort().join(" "));
