var __defProp = Object.defineProperty;
var __export = (target, all) => {
  for (var name in all)
    __defProp(target, name, { get: all[name], enumerable: true });
};

// ../pele/src/detect.ts
var DETECTORS = [
  ["flowchart", /^\s*flowchart-elk/],
  ["mindmap", /^\s*mindmap/],
  ["architecture", /^\s*architecture/],
  ["agentflow", /^\s*agentflow-beta\b/],
  ["c4", /^\s*C4Context|C4Container|C4Component|C4Dynamic|C4Deployment/],
  ["kanban", /^\s*kanban/],
  ["class", /^\s*classDiagram/],
  ["er", /^\s*erDiagram/],
  ["gantt", /^\s*gantt/],
  ["info", /^\s*info/],
  ["pie", /^\s*pie/],
  ["requirement", /^\s*requirement(Diagram)?/],
  ["sequence", /^\s*sequenceDiagram/],
  ["swimlane", /^\s*swimlane-beta\b/],
  ["flowchart", /^\s*(graph|flowchart)/],
  ["timeline", /^\s*timeline/],
  ["gitGraph", /^\s*gitGraph/],
  ["state", /^\s*stateDiagram/],
  ["journey", /^\s*journey/],
  ["quadrantChart", /^\s*quadrantChart/],
  ["sankey", /^\s*sankey(-beta)?/],
  ["packet", /^\s*packet(-beta)?/],
  ["xychart", /^\s*xychart(-beta)?/],
  ["block", /^\s*block(-beta)?/],
  ["eventmodeling", /^\s*eventmodeling/],
  ["treeView", /^\s*treeView-beta/],
  ["radar", /^\s*radar-beta/],
  ["ishikawa", /^\s*ishikawa(-beta)?\b/i],
  ["treemap", /^\s*treemap/],
  ["railroad", /^\s*railroad-(?:ebnf-|abnf-|peg-)?beta/i],
  ["venn", /^\s*venn-beta/],
  ["wardley", /^\s*wardley-beta/i],
  ["cynefin", /^\s*cynefin-beta(?:[\s:]|$)/],
  ["usecase", /^\s*usecase-beta(?:\s|$)/]
];
function detect(cleaned) {
  for (const [type, re] of DETECTORS) if (re.test(cleaned)) return type;
  return null;
}

// ../pele/src/errors.ts
var PeleError = class extends Error {
  code;
  type;
  line;
  column;
  snippet;
  constructor(message, code = "syntax", info = {}) {
    super(message);
    this.name = "PeleError";
    this.code = code;
    this.type = info.type ?? null;
    this.line = info.line ?? 0;
    this.column = info.column ?? 0;
    this.snippet = info.snippet ?? "";
  }
};
function locate(src, offset) {
  let line = 1;
  let lineStart = 0;
  for (let i = src.indexOf("\n"); i !== -1 && i < offset; i = src.indexOf("\n", i + 1)) {
    line++;
    lineStart = i + 1;
  }
  let lineEnd = src.indexOf("\n", offset);
  if (lineEnd === -1) lineEnd = src.length;
  const column = offset - lineStart + 1;
  const text = src.slice(lineStart, lineEnd);
  return { line, column, snippet: text + "\n" + "-".repeat(column - 1) + "^" };
}
function syntaxError(type, src, offset, detail, lexical = false) {
  const loc = locate(src, offset);
  const head = lexical ? `Lexical error on line ${loc.line}. Unrecognized text.` : `Parse error on line ${loc.line}:`;
  const message = lexical ? `${head}
${loc.snippet}` : `${head}
${loc.snippet}
${detail}`;
  return new PeleError(message, "syntax", { type, ...loc });
}

// ../pele/src/util/yaml.ts
var Incomplete = class {
  constructor(message) {
    this.message = message;
  }
  message;
};
var MAX_DEPTH = 64;
function assign(target, key, value) {
  if (key === "__proto__") {
    Object.defineProperty(target, key, { value, enumerable: true, writable: true, configurable: true });
  } else {
    target[key] = value;
  }
}
var Reader = class {
  lines;
  i = 0;
  constructor(src) {
    this.lines = src.replace(/\r\n?/g, "\n").split("\n");
  }
  skipBlank() {
    while (this.i < this.lines.length) {
      const t = this.lines[this.i].trim();
      if (t !== "" && t[0] !== "#" && t !== "---") break;
      this.i++;
    }
  }
  indent() {
    const line = this.lines[this.i];
    let k = 0;
    while (k < line.length && line[k] === " ") k++;
    if (line[k] === "	") throw new Error("YAML: tabs are not allowed for indentation");
    return k;
  }
};
var RE_INT = /^[-+]?(?:0|[1-9][0-9]*)$/;
var RE_HEX = /^[-+]?0x[0-9a-fA-F]+$/;
var RE_OCT = /^[-+]?0o[0-7]+$/;
var RE_BIN = /^[-+]?0b[01]+$/;
var RE_FLOAT = /^[-+]?(?:[0-9][0-9]*(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][-+]?[0-9]+)?$/;
var RE_KEY_START = /[\s#:,[\]{}&*!|>'"%@`]/;
function isBlank(ch) {
  return ch === " " || ch === "	" || ch === "\n" || ch === "\r";
}
function splitKey(line) {
  const first = line[0];
  let end;
  let colon;
  if (first === '"' || first === "'") {
    let i = 1;
    while (true) {
      if (i >= line.length) return null;
      const c = line[i];
      if (first === '"' && c === "\\") {
        i += 2;
      } else if (c === first) {
        if (first === '"' || line[i + 1] !== "'") break;
        i += 2;
      } else {
        i++;
      }
    }
    end = i + 1;
    colon = end;
    while (isBlank(line[colon])) colon++;
    if (line[colon] !== ":") return null;
  } else {
    if (first === void 0 || RE_KEY_START.test(first)) return null;
    if (first === "-" && (line.length < 2 || isBlank(line[1]) || line[1] === ":" || line[1] === "#")) return null;
    colon = 1;
    while (colon < line.length && line[colon] !== ":" && line[colon] !== "#") colon++;
    if (line[colon] !== ":") return null;
    end = colon;
    while (isBlank(line[end - 1])) end--;
  }
  let length = colon + 1;
  if (length < line.length && !isBlank(line[length])) return null;
  while (isBlank(line[length])) length++;
  return { key: line.slice(0, end), length };
}
function scalar(raw) {
  const s = raw.trim();
  if (s === "" || s === "~" || s === "null" || s === "Null" || s === "NULL") return null;
  if (s === "true" || s === "True" || s === "TRUE") return true;
  if (s === "false" || s === "False" || s === "FALSE") return false;
  if (RE_INT.test(s) || RE_FLOAT.test(s)) return Number(s);
  if (RE_HEX.test(s) || RE_OCT.test(s) || RE_BIN.test(s)) {
    const neg = s[0] === "-";
    const v = Number(neg || s[0] === "+" ? s.slice(1) : s);
    return neg ? -v : v;
  }
  if (/^[-+]?\.(?:inf|Inf|INF)$/.test(s)) return s[0] === "-" ? -Infinity : Infinity;
  if (/^\.(?:nan|NaN|NAN)$/.test(s)) return NaN;
  return s;
}
var ESCAPES = {
  "0": "\0",
  a: "\x07",
  b: "\b",
  t: "	",
  n: "\n",
  v: "\v",
  f: "\f",
  r: "\r",
  e: "\x1B",
  " ": " ",
  '"': '"',
  "/": "/",
  "\\": "\\",
  N: "\x85",
  _: "\xA0",
  L: "\u2028",
  P: "\u2029"
};
function fold(text) {
  if (!text.includes("\n")) return text;
  const lines = text.split("\n");
  const last = lines.length - 1;
  for (let i = 0; i <= last; i++) {
    const line = lines[i];
    let from = 0;
    let to = line.length;
    if (i > 0) while (from < to && (line.charCodeAt(from) === 32 || line.charCodeAt(from) === 9)) from++;
    if (i < last) while (to > from && (line.charCodeAt(to - 1) === 32 || line.charCodeAt(to - 1) === 9)) to--;
    lines[i] = line.slice(from, to);
  }
  return lines.join("\n").replace(/\n+/g, (m) => m.length === 1 ? " " : "\n".repeat(m.length - 1));
}
function unquoteDouble(body) {
  let out = "";
  const s = fold(body.replace(/\\\n[ \t]*/g, ""));
  for (let i = 0; i < s.length; i++) {
    const ch = s[i];
    if (ch !== "\\") {
      out += ch;
      continue;
    }
    const e = s[++i];
    if (e === "x" || e === "u" || e === "U") {
      const len = e === "x" ? 2 : e === "u" ? 4 : 8;
      out += String.fromCodePoint(parseInt(s.slice(i + 1, i + 1 + len), 16));
      i += len;
    } else if (e in ESCAPES) {
      out += ESCAPES[e];
    } else {
      throw new Error("YAML: unknown escape sequence");
    }
  }
  return out;
}
function skip(s, pos, commas) {
  while (pos < s.length) {
    const c = s[pos];
    if (c === " " || c === "\n" || c === "	" || c === "\r" || commas && c === ",") {
      pos++;
    } else if (c === "#" && (pos === 0 || /\s/.test(s[pos - 1]))) {
      const nl = s.indexOf("\n", pos);
      pos = nl === -1 ? s.length : nl;
    } else {
      break;
    }
  }
  return pos;
}
function entryStart(ch, separated) {
  if (!separated) throw new Error("YAML: missed comma between flow collection entries");
  if (ch === ",") throw new Error("YAML: expected the node content, but found ','");
}
function flow(s, pos, stops, depth = 0) {
  if (depth > MAX_DEPTH) throw new Error("YAML: nesting is too deep");
  pos = skip(s, pos, false);
  const ch = s[pos];
  if (ch === "{") {
    const out = {};
    pos++;
    let separated = true;
    while (true) {
      pos = skip(s, pos, false);
      if (pos >= s.length) throw new Incomplete("YAML: unexpected end of the stream within a flow collection");
      if (s[pos] === "}") return [out, pos + 1];
      entryStart(s[pos], separated);
      let key;
      [key, pos] = flow(s, pos, ":,}", depth + 1);
      pos = skip(s, pos, false);
      let value = null;
      if (s[pos] === ":") [value, pos] = flow(s, pos + 1, ",}", depth + 1);
      assign(out, String(key), value);
      pos = skip(s, pos, false);
      separated = s[pos] === ",";
      if (separated) pos++;
    }
  }
  if (ch === "[") {
    const out = [];
    pos++;
    let separated = true;
    while (true) {
      pos = skip(s, pos, false);
      if (pos >= s.length) throw new Incomplete("YAML: unexpected end of the stream within a flow collection");
      if (s[pos] === "]") return [out, pos + 1];
      entryStart(s[pos], separated);
      let value;
      [value, pos] = flow(s, pos, ",]", depth + 1);
      out.push(value);
      pos = skip(s, pos, false);
      separated = s[pos] === ",";
      if (separated) pos++;
    }
  }
  if (ch === '"') {
    let end2 = pos + 1;
    while (end2 < s.length && s[end2] !== '"') end2 += s[end2] === "\\" ? 2 : 1;
    if (end2 >= s.length) throw new Incomplete("YAML: unexpected end of the stream within a double quoted scalar");
    return [unquoteDouble(s.slice(pos + 1, end2)), end2 + 1];
  }
  if (ch === "'") {
    let end2 = pos + 1;
    while (end2 < s.length) {
      if (s[end2] === "'") {
        if (s[end2 + 1] !== "'") break;
        end2++;
      }
      end2++;
    }
    if (end2 >= s.length) throw new Incomplete("YAML: unexpected end of the stream within a single quoted scalar");
    return [fold(s.slice(pos + 1, end2)).replace(/''/g, "'"), end2 + 1];
  }
  let end = pos;
  while (end < s.length) {
    const c = s[end];
    if (stops.includes(c) && (c !== ":" || end + 1 >= s.length || /[\s,[\]{}]/.test(s[end + 1]))) break;
    if (c === "#" && end > pos && /\s/.test(s[end - 1])) {
      const nl = s.indexOf("\n", end);
      return [scalar(fold(s.slice(pos, end).trim())), nl === -1 ? s.length : nl];
    }
    end++;
  }
  return [scalar(fold(s.slice(pos, end).trim())), end];
}
function stripComment(s) {
  let quote = "";
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (quote) {
      if (c === "\\" && quote === '"') i++;
      else if (c === quote) quote = "";
    } else if (c === '"' || c === "'") {
      quote = c;
    } else if (c === "#" && (i === 0 || s[i - 1] === " " || s[i - 1] === "	")) {
      return s.slice(0, i);
    }
  }
  return s;
}
function blockScalar(r, header, parentIndent) {
  const folded = header[0] === ">";
  const chomp = header.includes("-") ? "-" : header.includes("+") ? "+" : "";
  const explicit = /[1-9]/.exec(header);
  let indent = explicit ? parentIndent + Number(explicit[0]) : -1;
  const out = [];
  while (r.i < r.lines.length) {
    const line = r.lines[r.i];
    if (line.trim() === "") {
      out.push("");
      r.i++;
      continue;
    }
    const k = line.length - line.trimStart().length;
    if (indent === -1) {
      if (k <= parentIndent) break;
      indent = k;
    }
    if (k < indent) break;
    out.push(line.slice(indent));
    r.i++;
  }
  let trailing = 0;
  while (out.length > 0 && out[out.length - 1] === "") {
    out.pop();
    trailing++;
  }
  let text;
  if (folded) {
    text = "";
    for (let i = 0; i < out.length; i++) {
      const cur = out[i];
      if (i > 0) {
        const prev = out[i - 1];
        if (cur === "") text += "\n";
        else if (prev !== "") text += /^[ \t]/.test(prev) || /^[ \t]/.test(cur) ? "\n" : " ";
      }
      text += cur;
    }
  } else {
    text = out.join("\n");
  }
  if (out.length === 0) return chomp === "+" ? "\n".repeat(trailing) : "";
  if (chomp === "-") return text;
  if (chomp === "+") return text + "\n".repeat(trailing + 1);
  return text + "\n";
}
function inlineValue(r, rest, indent) {
  const first = rest[0];
  if (first === "|" || first === ">") {
    if (/^[|>][-+0-9]*\s*(?:#.*)?$/.test(rest)) return blockScalar(r, rest, indent);
  }
  if (first === "{" || first === "[" || first === '"' || first === "'") {
    const start = r.i;
    let text2 = rest;
    let taken = 0;
    let batch = 1;
    while (true) {
      try {
        const [value, end] = flow(text2, 0, "");
        let used = 0;
        for (let k = text2.indexOf("\n"); k !== -1 && k < end; k = text2.indexOf("\n", k + 1)) used++;
        const lineEnd = text2.indexOf("\n", end);
        if (stripComment(text2.slice(end, lineEnd === -1 ? text2.length : lineEnd)).trim() !== "") {
          throw new Error("YAML: unexpected content after a value");
        }
        r.i = start + used;
        return value;
      } catch (err) {
        if (!(err instanceof Incomplete)) throw err;
        if (start + taken >= r.lines.length) throw new Error(err.message);
        const more = r.lines.slice(start + taken, start + taken + batch);
        text2 += "\n" + more.join("\n");
        taken += more.length;
        batch *= 2;
      }
    }
  }
  let text = stripComment(rest).trim();
  while (r.i < r.lines.length) {
    const line = r.lines[r.i];
    if (line.trim() === "") {
      let j = r.i;
      while (j < r.lines.length && r.lines[j].trim() === "") j++;
      if (j >= r.lines.length || r.lines[j].length - r.lines[j].trimStart().length <= indent) break;
      text += "\n".repeat(j - r.i);
      r.i = j;
      continue;
    }
    if (line.length - line.trimStart().length <= indent) break;
    const more = stripComment(line).trim();
    text += (text.endsWith("\n") ? "" : " ") + more;
    r.i++;
  }
  return scalar(text);
}
function block(r, minIndent, depth = 0) {
  if (depth > MAX_DEPTH) throw new Error("YAML: nesting is too deep");
  r.skipBlank();
  if (r.i >= r.lines.length) return null;
  const indent = r.indent();
  if (indent < minIndent) return null;
  const line = r.lines[r.i].slice(indent);
  if (line === "-" || line.startsWith("- ")) {
    const out2 = [];
    while (r.i < r.lines.length) {
      r.skipBlank();
      if (r.i >= r.lines.length || r.indent() !== indent) break;
      const cur = r.lines[r.i].slice(indent);
      if (cur !== "-" && !cur.startsWith("- ")) break;
      const rest = cur.slice(1);
      const pad = rest.length - rest.trimStart().length + 1;
      const body = stripComment(rest).trim();
      if (body === "") {
        r.i++;
        out2.push(block(r, indent + 1, depth + 1));
      } else if (splitKey(body) !== null) {
        r.lines[r.i] = " ".repeat(indent + pad) + rest.trimStart();
        out2.push(block(r, indent + pad, depth + 1));
      } else {
        r.i++;
        out2.push(inlineValue(r, rest.trim(), indent));
      }
    }
    return out2;
  }
  if (splitKey(line) === null) {
    r.i++;
    return inlineValue(r, line.trim(), indent - 1);
  }
  const out = {};
  while (r.i < r.lines.length) {
    r.skipBlank();
    if (r.i >= r.lines.length) break;
    const k = r.indent();
    if (k < indent) break;
    if (k > indent) throw new Error("YAML: bad indentation of a mapping entry");
    const cur = r.lines[r.i].slice(indent);
    const km = splitKey(cur);
    if (!km) throw new Error("YAML: can not read a block mapping entry");
    const key = String(flow(km.key, 0, "")[0]);
    const rest = cur.slice(km.length);
    r.i++;
    if (stripComment(rest).trim() === "") {
      r.skipBlank();
      if (r.i < r.lines.length) {
        const next = r.indent();
        const nextLine = r.lines[r.i].slice(next);
        const seq = nextLine === "-" || nextLine.startsWith("- ");
        assign(out, key, next > indent || seq && next === indent ? block(r, seq ? indent : indent + 1, depth + 1) : null);
      } else {
        assign(out, key, null);
      }
    } else {
      assign(out, key, inlineValue(r, rest.trim(), indent));
    }
  }
  return out;
}
function parseYaml(src) {
  const trimmed = src.trim();
  if (trimmed[0] === "{" || trimmed[0] === "[") {
    try {
      const [value2, end] = flow(trimmed, 0, "");
      if (skip(trimmed, end, false) === trimmed.length) return value2;
    } catch (err) {
      if (err instanceof Incomplete) throw new Error(err.message);
      throw err;
    }
  }
  const r = new Reader(src);
  const value = block(r, 0);
  r.skipBlank();
  if (r.i < r.lines.length && r.lines[r.i].trim() !== "...") {
    throw new Error("YAML: end of the stream or a document separator is expected");
  }
  return value;
}

// ../pele/src/util/chars.ts
function isSpace(c) {
  if (c < 128) return c === 32 || c >= 9 && c <= 13;
  return c === 160 || c === 5760 || c >= 8192 && c <= 8202 || c === 8232 || c === 8233 || c === 8239 || c === 8287 || c === 12288 || c === 65279;
}

// ../pele/src/preprocess.ts
var RE_COMMENT = /^\s*%%(?!{)[^\n]+\n?/gm;
var RE_DIRECTIVE = /%{2}{\s*(?:(\w+)\s*:|(\w+))\s*(?:(\w+)|((?:(?!}%{2}).|\r?\n)*))?\s*(?:}%{2})?/gi;
var RE_HTML_TAG = /<(\w+)([^>]*)>/g;
var RE_ATTR = /="([^"]*)"/g;
var UNSAFE_KEYS = /* @__PURE__ */ new Set(["__proto__", "constructor", "prototype"]);
function cleanupComments(text) {
  return (text.includes("%%") ? text.replace(RE_COMMENT, "") : text).trimStart();
}
function isLineBreak(ch) {
  return ch === "\n" || ch === "\r";
}
function isSpace2(ch) {
  return ch !== void 0 && /\s/.test(ch);
}
function matchFrontMatter(text) {
  let cursor = 0;
  while (cursor < text.length && isSpace2(text[cursor]) && !isLineBreak(text[cursor])) cursor++;
  const indent = text.slice(0, cursor);
  if (!text.startsWith("---", cursor)) return void 0;
  const openingEnds = [];
  for (let i = cursor + 3; i < text.length && isSpace2(text[i]); i++) {
    if (isLineBreak(text[i])) openingEnds.push(i);
  }
  if (openingEnds.length === 0) return void 0;
  const fence = indent + "---";
  const closingEnd = (i) => {
    if (!isLineBreak(text[i]) || !text.startsWith(fence, i + 1)) return -1;
    let last = -1;
    for (let j = i + 1 + fence.length; j < text.length && isSpace2(text[j]); j++) {
      if (isLineBreak(text[j])) last = j;
    }
    return last;
  };
  const needle = "\n" + fence;
  let lastFence = -1;
  for (let i = text.lastIndexOf(needle); i > openingEnds[0]; i = text.lastIndexOf(needle, i - 1)) {
    if (closingEnd(i) !== -1) {
      lastFence = i;
      break;
    }
  }
  if (lastFence === -1) return void 0;
  let openingEnd = openingEnds[0];
  for (const candidate of openingEnds) if (candidate < lastFence) openingEnd = candidate;
  const bodyStart = openingEnd + 1;
  const firstAt = bodyStart > 0 ? bodyStart - 1 : 0;
  for (let i = text.indexOf(needle, firstAt); i !== -1 && i <= lastFence; i = text.indexOf(needle, i + 1)) {
    if (i < bodyStart) continue;
    const end = closingEnd(i);
    if (end !== -1) return { indent, body: text.slice(bodyStart, i), length: end + 1 };
  }
  return void 0;
}
function isRecord(value) {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
function merge(target, source) {
  for (const key of Object.keys(source)) {
    if (UNSAFE_KEYS.has(key)) continue;
    const value = source[key];
    const current = target[key];
    if (isRecord(value) && isRecord(current)) merge(current, value);
    else if (isRecord(value)) target[key] = merge({}, value);
    else target[key] = value;
  }
  return target;
}
function directives(text, config) {
  if (!text.includes("%%{")) return text;
  const normalized = text.trim().replace(/'/g, '"');
  RE_DIRECTIVE.lastIndex = 0;
  let match;
  while ((match = RE_DIRECTIVE.exec(normalized)) !== null) {
    if (match.index === RE_DIRECTIVE.lastIndex) RE_DIRECTIVE.lastIndex++;
    const type = match[1] ?? match[2];
    if (type === "wrap") {
      config.wrap = true;
    } else if ((type === "init" || type === "initialize") && match[4]) {
      try {
        const args = JSON.parse(match[4].trim());
        if (isRecord(args)) merge(config, args);
      } catch {
      }
    }
  }
  RE_DIRECTIVE.lastIndex = 0;
  return text.replace(RE_DIRECTIVE, "");
}
function preprocess(source) {
  let text = source.includes("\r") ? source.replace(/\r\n?/g, "\n") : source;
  const lastClose = text.includes("<") ? text.lastIndexOf(">") : -1;
  if (lastClose > 0) {
    text = text.slice(0, lastClose + 1).replace(RE_HTML_TAG, (_m, tag, attrs) => "<" + tag + attrs.replace(RE_ATTR, "='$1'") + ">") + text.slice(lastClose + 1);
  }
  const config = {};
  let title;
  let lineOffset = 0;
  const front = text.includes("---") ? matchFrontMatter(text) : void 0;
  if (front) {
    const body = front.indent ? front.body.split("\n").map((line) => line.startsWith(front.indent) ? line.slice(front.indent.length) : line).join("\n") : front.body;
    let parsed;
    try {
      parsed = parseYaml(body);
    } catch (error) {
      throw new PeleError(`Front matter is not valid YAML. ${error.message}`, "syntax");
    }
    if (isRecord(parsed)) {
      if (parsed.title) title = String(parsed.title);
      if (isRecord(parsed.config)) merge(config, parsed.config);
      if (parsed.displayMode) {
        const gantt = isRecord(config.gantt) ? config.gantt : config.gantt = {};
        gantt.displayMode = String(parsed.displayMode);
      }
    }
    for (let i = text.indexOf("\n"); i !== -1 && i < front.length; i = text.indexOf("\n", i + 1)) lineOffset++;
    text = text.slice(front.length);
  }
  text = directives(text, config);
  return { text: cleanupComments(text), title, config, withComments: text, lineOffset };
}
var RE_ENTITY = /#\w+;/g;
var RE_INT2 = /^\+?\d+$/;
var RE_LINE_BREAK = new RegExp("[\\n\\r" + String.fromCharCode(8232, 8233) + "]", "g");
function dropColorSemicolons(text, keyword2) {
  let from = text.indexOf(keyword2);
  if (from === -1) return text;
  let out = "";
  let last = 0;
  while (from !== -1) {
    RE_LINE_BREAK.lastIndex = from;
    const lineEnd = RE_LINE_BREAK.test(text) ? RE_LINE_BREAK.lastIndex - 1 : text.length;
    let afterColon = false;
    let hash = -1;
    for (let j = from + keyword2.length; j < lineEnd; j++) {
      const c = text.charCodeAt(j);
      if (c === 58) {
        afterColon = true;
      } else if (c === 35) {
        if (afterColon) {
          hash = j;
          break;
        }
      } else if (isSpace(c)) {
        afterColon = false;
      }
    }
    if (hash !== -1) {
      const semi = text.lastIndexOf(";", lineEnd - 1);
      if (semi > hash) {
        out += text.slice(last, semi);
        last = semi + 1;
      }
    }
    from = lineEnd >= text.length ? -1 : text.indexOf(keyword2, lineEnd + 1);
  }
  return last === 0 ? text : out + text.slice(last);
}
function encodeEntities(text) {
  if (!text.includes("#")) return text;
  return dropColorSemicolons(dropColorSemicolons(text, "style"), "classDef").replace(RE_ENTITY, (s) => {
    const inner = s.substring(1, s.length - 1);
    return (RE_INT2.test(inner) ? "\uFB02\xB0\xB0" : "\uFB02\xB0") + inner + "\xB6\xDF";
  });
}

// ../pele/src/text/entities.ts
var NAMED = "amp:38,lt:60,gt:62,quot:34,apos:39,nbsp:160,iexcl:161,cent:162,pound:163,curren:164,yen:165,sect:167,copy:169,laquo:171,not:172,reg:174,deg:176,plusmn:177,sup2:178,sup3:179,micro:181,para:182,middot:183,sup1:185,raquo:187,frac14:188,frac12:189,frac34:190,iquest:191,times:215,divide:247,ndash:8211,mdash:8212,lsquo:8216,rsquo:8217,sbquo:8218,ldquo:8220,rdquo:8221,bdquo:8222,dagger:8224,Dagger:8225,bull:8226,hellip:8230,permil:8240,prime:8242,Prime:8243,lsaquo:8249,rsaquo:8250,euro:8364,trade:8482,larr:8592,uarr:8593,rarr:8594,darr:8595,harr:8596,lArr:8656,uArr:8657,rArr:8658,dArr:8659,hArr:8660,forall:8704,part:8706,exist:8707,empty:8709,nabla:8711,isin:8712,notin:8713,ni:8715,prod:8719,sum:8721,minus:8722,radic:8730,infin:8734,and:8743,or:8744,cap:8745,cup:8746,int:8747,there4:8756,sim:8764,cong:8773,asymp:8776,ne:8800,equiv:8801,le:8804,ge:8805,sub:8834,sup:8835,sube:8838,supe:8839,oplus:8853,otimes:8855,perp:8869,sdot:8901,loz:9674,spades:9824,clubs:9827,hearts:9829,diams:9830,check:10003,cross:10007,equals:61,colon:58,semi:59,num:35,percnt:37,lpar:40,rpar:41,lbrack:91,rbrack:93,lbrace:123,rbrace:125,vert:124,sol:47,bsol:92,quest:63,excl:33,ast:42,plus:43,comma:44,period:46,dollar:36,commat:64,grave:96,hat:94,lowbar:95,tilde:126,Alpha:913,Beta:914,Gamma:915,Delta:916,Epsilon:917,Zeta:918,Eta:919,Theta:920,Iota:921,Kappa:922,Lambda:923,Mu:924,Nu:925,Xi:926,Omicron:927,Pi:928,Rho:929,Sigma:931,Tau:932,Upsilon:933,Phi:934,Chi:935,Psi:936,Omega:937,alpha:945,beta:946,gamma:947,delta:948,epsilon:949,zeta:950,eta:951,theta:952,iota:953,kappa:954,lambda:955,mu:956,nu:957,xi:958,omicron:959,pi:960,rho:961,sigmaf:962,sigma:963,tau:964,upsilon:965,phi:966,chi:967,psi:968,omega:969";
var named;
function lookup(name) {
  if (!named) {
    named = /* @__PURE__ */ new Map();
    for (const pair of NAMED.split(",")) {
      const at = pair.indexOf(":");
      named.set(pair.slice(0, at), String.fromCodePoint(Number(pair.slice(at + 1))));
    }
  }
  return named.get(name);
}
function codePoint(n, fallback) {
  return n > 0 && n <= 1114111 && (n < 55296 || n > 57343) ? String.fromCodePoint(n) : fallback;
}
var RE_ENTITY2 = /&(?:#(\d+)|#[xX]([0-9a-fA-F]+)|(\w+));/g;
var OPEN = "\uFB02\xB0";
var CLOSE = "\xB6\xDF";
function decodePlaceholders(text) {
  let out = "";
  let last = 0;
  let from = text.indexOf(OPEN);
  while (from !== -1) {
    const stop = text.indexOf("\xB6", from + 2);
    if (stop === -1) break;
    if (text[stop + 1] === "\xDF") {
      const numeric = text[from + 2] === "\xB0";
      const body = text.slice(from + (numeric ? 3 : 2), stop);
      const raw = text.slice(from, stop + 2);
      out += text.slice(last, from) + (numeric ? codePoint(Number(body), raw) : lookup(body) ?? "&" + body + ";");
      last = stop + 2;
    }
    from = text.indexOf(OPEN, stop + 1);
  }
  return last === 0 ? text : out + text.slice(last);
}
function decodeEntities(text) {
  if (text.includes(CLOSE)) text = decodePlaceholders(text);
  if (text.includes("&")) {
    text = text.replace(RE_ENTITY2, (m, dec, hex, name) => {
      if (dec) return codePoint(Number(dec), m);
      if (hex) return codePoint(parseInt(hex, 16), m);
      return lookup(name) ?? m;
    });
  }
  return text;
}

// ../pele/src/text/metrics.ts
var REGULAR = "7q7q9vfgfgopij5b9999atg87q997q7qfgfgfgfgfgfgfgfgfgfg7q7qg8g8g8fgs7ijijk2k2ijgzlmk27qdwijfgn5k2lmijlmk2ijgzk2ijq8ijijgz7q7q7qd1fg99fgfgdwfgfg7qfgfg6666dw66n5fgfgfgfg99dw7qfgdwk2dwdwdw9a789ag8";
var BOLD = "7q99d6fgfgopk26m9999atg87q997q7qfgfgfgfgfgfgfgfgfgfg9999g8g8g8gzr3k2k2k2k2ijgzlmk27qfgk2gzn5k2lmijlmk2ijgzk2ijq8ijijgz997q99g8fg99fggzfggzfg99gzgz7q7qfg7qopgzgzgzgzatfg99gzfglmfgfgdwat7satg8";

// ../pele/src/text/measurer.ts
function unpack(packed) {
  const out = new Uint16Array(95);
  for (let i = 0; i < 95; i++) out[i] = parseInt(packed.substr(i * 2, 2), 36);
  return out;
}
var regular;
var bold;
function isWide(c) {
  return c >= 4352 && c <= 4447 || c >= 11904 && c <= 42191 || c >= 44032 && c <= 55203 || c >= 63744 && c <= 64255 || c >= 65072 && c <= 65103 || c >= 65280 && c <= 65376 || c >= 65504 && c <= 65510 || c >= 55296 && c <= 56319;
}
function isZeroWidth(c) {
  return c >= 768 && c <= 879 || c >= 8203 && c <= 8207 || c >= 56320 && c <= 57343 || c >= 65024 && c <= 65039 || c === 65279;
}
var metricsMeasurer = {
  width(text, size, style) {
    const mono = (style & 4 /* Mono */) !== 0;
    const table = style & 1 /* Bold */ ? bold ??= unpack(BOLD) : regular ??= unpack(REGULAR);
    let units = 0;
    for (let i = 0; i < text.length; i++) {
      const c = text.charCodeAt(i);
      if (c < 32) continue;
      if (c < 127) units += mono ? 600 : table[c - 32];
      else if (isZeroWidth(c)) continue;
      else if (isWide(c)) units += mono ? 1200 : 1e3;
      else units += mono ? 600 : 580;
    }
    return units * size / 1e3;
  }
};

// ../pele/src/text/icons.ts
var resolver;
var found = /* @__PURE__ */ new Map();
function iconMarkup(name) {
  if (resolver === void 0) return "";
  let markup = found.get(name);
  if (markup === void 0) {
    markup = resolver(name) ?? "";
    found.set(name, markup);
  }
  return markup;
}

// ../pele/src/text/label.ts
var LINE_HEIGHT = 1.5;
var RE_BREAK = /<br\s*\/?>|\\n|\n/gi;
var RE_TAG = /<\/?([a-zA-Z][\w-]*)(?:\s[^<>]*)?\/?>/y;
var RE_ICON = /(fa[bklrs]?):fa-([\w-]+)/g;
var RE_MARKUP = /[<\\\n*_]|fa[bklrs]?:fa-/;
var EMPTY = { lines: [], widths: [], width: 0, height: 0, size: 0, lineHeight: 0 };
function dedent(text) {
  const lines = text.split("\n");
  let min = Infinity;
  for (const line of lines) {
    if (line.trim() === "") continue;
    const indent = line.length - line.trimStart().length;
    if (indent < min) min = indent;
  }
  if (min === Infinity || min === 0) return text;
  return lines.map((line) => line.slice(Math.min(min, line.length - line.trimStart().length))).join("\n");
}
function pushText(line, text, style) {
  if (text === "") return;
  const decoded = decodeEntities(text);
  const last = line[line.length - 1];
  if (last && last.style === style && last.icon === void 0) last.text += decoded;
  else line.push({ text: decoded, style, width: 0 });
}
function pushIcons(line, text, style) {
  if (!text.includes(":fa-")) {
    pushText(line, text, style);
    return;
  }
  let last = 0;
  RE_ICON.lastIndex = 0;
  let m;
  while ((m = RE_ICON.exec(text)) !== null) {
    const icon = `${m[1]}:fa-${m[2]}`;
    pushText(line, text.slice(last, m.index), style);
    last = m.index + m[0].length;
    if (iconMarkup(icon) !== "") {
      line.push({ text: "", style, icon, width: 0 });
    } else if (text[last] === " ") {
      last++;
    } else if (last === text.length) {
      const before = line[line.length - 1];
      if (before && before.icon === void 0) before.text = before.text.trimEnd();
    }
  }
  pushText(line, text.slice(last), style);
}
function inline(line, source, markdown, base) {
  let style = base;
  let marked = 0;
  let text = "";
  let i = 0;
  const n = source.length;
  const flush = () => {
    pushIcons(line, text, style);
    text = "";
  };
  while (i < n) {
    const ch = source[i];
    if (ch === "<") {
      RE_TAG.lastIndex = i;
      const m = RE_TAG.exec(source);
      if (m && m.index === i) {
        const tag = m[1].toLowerCase();
        const closing = source[i + 1] === "/";
        const bit = tag === "b" || tag === "strong" ? 1 /* Bold */ : tag === "i" || tag === "em" ? 2 /* Italic */ : 0;
        if (bit) {
          flush();
          style = closing ? style & ~bit | base & bit : style | bit;
        }
        i += m[0].length;
        continue;
      }
    } else if (markdown && (ch === "*" || ch === "_")) {
      const double = source[i + 1] === ch;
      const bit = double ? 1 /* Bold */ : 2 /* Italic */;
      const len = double ? 2 : 1;
      const open = (marked & bit) === 0;
      const prev = i > 0 ? source[i - 1] : " ";
      const next = i + len < n ? source[i + len] : " ";
      const canOpen = open && next.trim() !== "" && source.indexOf(double ? ch + ch : ch, i + len) !== -1;
      const canClose = !open && prev.trim() !== "";
      const wordy = ch === "_" && /\w/.test(prev) && /\w/.test(next);
      if ((canOpen || canClose) && !wordy) {
        flush();
        marked = canOpen ? marked | bit : marked & ~bit;
        style = canOpen ? style | bit : style & ~bit | base & bit;
        i += len;
        continue;
      }
    }
    text += ch;
    i++;
  }
  flush();
}
function measureSpans(line, measurer, size) {
  let total = 0;
  for (const span of line) {
    span.width = span.icon !== void 0 ? size * 1.25 : measurer.width(span.text, size, span.style);
    total += span.width;
  }
  return total;
}
function wrap(line, measurer, size, max, out, widths) {
  let current = [];
  let width = 0;
  const commit = () => {
    const last = current[current.length - 1];
    if (last && last.icon === void 0) {
      const trimmed = last.text.trimEnd();
      if (trimmed !== last.text) {
        last.text = trimmed;
        const w = measurer.width(trimmed, size, last.style);
        width += w - last.width;
        last.width = w;
      }
    }
    out.push(current);
    widths.push(width);
    current = [];
    width = 0;
  };
  for (const span of line) {
    if (span.icon !== void 0) {
      const w = size * 1.25;
      if (width > 0 && width + w > max) commit();
      current.push({ ...span, width: w });
      width += w;
      continue;
    }
    for (const piece of span.text.match(/\s*\S+\s*|\s+/g) ?? []) {
      let part = piece;
      if (width > 0 && width + measurer.width(part.trimEnd(), size, span.style) > max) {
        commit();
        part = part.trimStart();
      }
      const last = current[current.length - 1];
      if (last && last.style === span.style && last.icon === void 0) {
        last.text += part;
        last.width = measurer.width(last.text, size, last.style);
        width = 0;
        for (const s of current) width += s.width;
      } else {
        const w = measurer.width(part, size, span.style);
        current.push({ text: part, style: span.style, width: w });
        width += w;
      }
    }
  }
  if (current.length > 0 || out.length === 0) commit();
}
function layoutLabel(raw, markdown, measurer, size, maxWidth, base = 0) {
  if (raw === void 0 || raw === "") return EMPTY;
  const lineHeight = Math.round(size * LINE_HEIGHT);
  if (!RE_MARKUP.test(raw) && !raw.includes("&") && !raw.includes("\uFB02")) {
    const text = raw.trim();
    const width2 = measurer.width(text, size, base);
    if (width2 <= maxWidth || !text.includes(" ")) {
      return {
        lines: [[{ text, style: base, width: width2 }]],
        widths: [width2],
        width: width2,
        height: lineHeight,
        size,
        lineHeight
      };
    }
    const lines2 = [];
    const widths2 = [];
    wrap([{ text, style: base, width: width2 }], measurer, size, maxWidth, lines2, widths2);
    let widest = 0;
    for (const w of widths2) if (w > widest) widest = w;
    return { lines: lines2, widths: widths2, width: widest, height: lines2.length * lineHeight, size, lineHeight };
  }
  const source = markdown ? dedent(raw.replace(/\r\n?/g, "\n")).trim() : raw.trim();
  const lines = [];
  const widths = [];
  let width = 0;
  for (const part of source.split(RE_BREAK)) {
    const line = [];
    inline(line, part.trim(), markdown, base);
    const w = measureSpans(line, measurer, size);
    if (w > maxWidth) {
      wrap(line, measurer, size, maxWidth, lines, widths);
    } else {
      lines.push(line);
      widths.push(w);
    }
  }
  for (const w of widths) if (w > width) width = w;
  return { lines, widths, width, height: lines.length * lineHeight, size, lineHeight };
}

// ../pele/src/svg/builder.ts
var RE_NEEDS_ESC = /[&<>"\x00-\x08\x0b\x0c\x0e-\x1f\x7f]/;
var RE_ESC = /[&<>"]|[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]+/g;
var ESC = { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" };
function esc(text) {
  return RE_NEEDS_ESC.test(text) ? text.replace(RE_ESC, (c) => ESC[c] ?? "") : text;
}
function escText(text) {
  return esc(decodeEntities(text));
}
var FRACTION = [];
for (let i = 0; i < 100; i++) FRACTION.push(i === 0 ? "" : i % 10 === 0 ? "." + i / 10 : "." + (i < 10 ? "0" + i : i));
function num(value) {
  const n = Math.round(value * 100);
  if (n === 0) return "0";
  if (n > 0) {
    if (n < 1e11) {
      const q = Math.floor(n / 100);
      return q + FRACTION[n - q * 100];
    }
  } else if (n > -1e11) {
    const m = -n;
    const q = Math.floor(m / 100);
    return "-" + q + FRACTION[m - q * 100];
  }
  return String(n / 100);
}
function labelSvg(label, cx, cy, attrs, icons) {
  const lines = label.lines;
  if (lines.length === 0) return "";
  const lh = label.lineHeight;
  const shift = label.size * 0.35;
  let y = cy - label.height / 2 + lh / 2 + shift;
  if (lines.length === 1 && lines[0].length === 1 && lines[0][0].icon === void 0 && lines[0][0].style === 0) {
    return `<text${attrs} x="${num(cx)}" y="${num(y)}" text-anchor="middle">${esc(lines[0][0].text)}</text>`;
  }
  const shared = sharedStyle(label);
  attrs = textStyle(attrs, shared);
  let body = "";
  let extra = "";
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    let hasIcon = false;
    for (const span of line) if (span.icon !== void 0) hasIcon = true;
    if (hasIcon) {
      let x = cx - label.widths[i] / 2;
      for (const span of line) {
        if (span.icon !== void 0) {
          const side = label.size * 1.1;
          const inner = icons?.(span.icon) ?? "";
          extra += `<svg class="pele-icon" data-icon="${escText(span.icon)}" x="${num(x)}" y="${num(
            y - shift - side / 2
          )}" width="${num(side)}" height="${num(side)}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">${inner}</svg>`;
        } else if (span.text !== "") {
          body += `<tspan x="${num(x)}" y="${num(y)}" text-anchor="start"${spanStyle(span.style & ~shared)}>${esc(span.text)}</tspan>`;
        }
        x += span.width;
      }
    } else {
      for (let j = 0; j < line.length; j++) {
        const span = line[j];
        const pos = j === 0 ? ` x="${num(cx)}" y="${num(y)}"` : "";
        body += `<tspan${pos}${spanStyle(span.style & ~shared)}>${esc(span.text)}</tspan>`;
      }
      if (line.length === 0) body += `<tspan x="${num(cx)}" y="${num(y)}"> </tspan>`;
    }
    y += lh;
  }
  return `<text${attrs} text-anchor="middle" xml:space="preserve">${body}</text>${extra}`;
}
var BOLD2 = ' font-weight="var(--_w)"';
var ITALIC = ' font-style="italic"';
function spanStyle(style) {
  return (style & 1 /* Bold */ ? BOLD2 : "") + (style & 2 /* Italic */ ? ITALIC : "");
}
function sharedStyle(label) {
  let shared = 1 /* Bold */ | 2 /* Italic */;
  let any = false;
  for (const line of label.lines) {
    for (const span of line) {
      if (span.icon !== void 0 || span.text === "") continue;
      shared &= span.style;
      any = true;
    }
  }
  return any ? shared : 0;
}
function textStyle(attrs, shared) {
  return attrs + (shared & 1 /* Bold */ && !attrs.includes(' font-weight="') ? BOLD2 : "") + (shared & 2 /* Italic */ && !attrs.includes(' font-style="') ? ITALIC : "");
}

// ../pele/src/svg/theme.ts
var ROOT_STYLE = "--_bg:var(--pele-bg,#fff);--_fg:var(--pele-fg,#1f1f1f);--_m:var(--pele-muted,#6e6e6e);--_l:var(--pele-line,#1f1f1f);--_s:var(--pele-surface,#f3f3f3);--_a:var(--pele-surface-alt,#e6e6e6);--_b:var(--pele-border,#8a8a8a);--_c:var(--pele-accent,#5b7bd5);color:var(--_fg)";
var RADIUS = "var(--pele-radius,4px)";
var CRITICAL = "var(--pele-critical,#e45756)";
var FAMILY = String.raw`(?:"[^"'\\\n<>{}();:@]*"|'[^"'\\\n<>{}();:@]*'|[\p{L}\p{N}_-][\p{L}\p{N}_ .-]*)`;
var RE_FAMILIES = new RegExp(String.raw`^\s*${FAMILY}(?:\s*,\s*${FAMILY})*\s*$`, "u");
function families(list, generic) {
  return list && RE_FAMILIES.test(list) ? list : generic;
}
function fontStyle(size, family, mono) {
  return `;--_fm:var(--pele-font-mono,${families(mono, "monospace")});font:${size}px sans-serif;letter-spacing:normal;word-spacing:normal;text-transform:none;direction:ltr;font-family:var(--pele-font,${families(family, "sans-serif")})`;
}
var SHAPE_PROPS = /* @__PURE__ */ new Set([
  "fill",
  "fill-opacity",
  "stroke",
  "stroke-width",
  "stroke-dasharray",
  "stroke-dashoffset",
  "stroke-linecap",
  "stroke-linejoin",
  "stroke-opacity",
  "opacity",
  "rx",
  "ry"
]);
var TEXT_PROPS = /* @__PURE__ */ new Set([
  "color",
  "font-size",
  "font-family",
  "font-weight",
  "font-style",
  "text-decoration",
  "letter-spacing",
  "word-spacing",
  "opacity"
]);
var RE_UNSAFE = /url\s*\(|expression\s*\(|javascript:|[<>{};\\]|@import|image-set|\/\*/i;
var NONE = {
  shape: "",
  line: "",
  text: "",
  stroke: void 0,
  bold: false,
  italic: false,
  fontSize: void 0
};
function resolveStyle(declarations) {
  if (declarations.length === 0) return NONE;
  const shape = /* @__PURE__ */ new Map();
  const text = /* @__PURE__ */ new Map();
  for (const decl of declarations) {
    const at = decl.indexOf(":");
    if (at <= 0) continue;
    const prop = decl.slice(0, at).trim().toLowerCase();
    const value = decl.slice(at + 1).replace(/!important/gi, "").trim();
    if (value === "" || RE_UNSAFE.test(value)) continue;
    if (SHAPE_PROPS.has(prop)) shape.set(prop, value);
    if (TEXT_PROPS.has(prop)) text.set(prop === "color" ? "fill" : prop, value);
  }
  if (shape.size === 0 && text.size === 0) return NONE;
  let shapeCss = "";
  let lineCss = "";
  for (const [prop, value] of shape) {
    shapeCss += `${prop}:${value};`;
    if (prop !== "fill" && prop !== "fill-opacity") lineCss += `${prop}:${value};`;
  }
  let textCss = "";
  for (const [prop, value] of text) textCss += `${prop}:${value};`;
  const weight = text.get("font-weight");
  const size = text.get("font-size");
  const px = size && /^[\d.]+(px)?$/.test(size) ? parseFloat(size) : void 0;
  return {
    shape: shapeCss ? ` style="${esc(shapeCss)}"` : "",
    line: lineCss ? ` style="${esc(lineCss)}"` : "",
    text: textCss ? ` style="${esc(textCss)}"` : "",
    stroke: shape.get("stroke"),
    bold: weight === "bold" || weight === "bolder" || Number(weight) >= 600,
    italic: text.get("font-style") === "italic",
    fontSize: px !== void 0 && px > 0 && px <= 200 ? px : void 0
  };
}
var RE_CLASS = /[^\w-]+/g;
function classNames(raw) {
  const cleaned = raw.replace(RE_CLASS, " ").trim();
  return cleaned === "" ? "" : " " + cleaned;
}
var SERIES = ["#4c78a8", "#f58518", "#54a24b", "#e45756", "#72b7b2", "#eeca3b", "#b279a2", "#9d755d"];
function seriesColor(index) {
  const k = (index % 8 + 8) % 8;
  return `var(--pele-series-${k + 1},${SERIES[k]})`;
}

// ../pele/src/svg/root.ts
var ROUNDED = ` rx="${RADIUS}"`;
var RE_ROUNDED = /<rect\b[^>]*>/g;
function rounded(content) {
  if (!content.includes(ROUNDED)) return content;
  return content.replace(RE_ROUNDED, (tag) => {
    const at = tag.indexOf(ROUNDED);
    if (at < 0) return tag;
    const rest = tag.slice(0, at) + tag.slice(at + ROUNDED.length);
    const style = rest.indexOf(' style="');
    if (style >= 0) return `${rest.slice(0, style + 8)}rx:var(--_r);${rest.slice(style + 8)}`;
    const end = rest.endsWith("/>") ? rest.length - 2 : rest.length - 1;
    return `${rest.slice(0, end)} style="rx:var(--_r)"${rest.slice(end)}`;
  });
}
var WEIGHTS = [
  ["--_tw", "title"],
  ["--_hw", "heading"],
  ["--_w", "bold"]
];
function svgDocument(type, width, height, fontSize, options, acc, content) {
  const prefix = esc(options.idPrefix ?? "pele");
  let head = "";
  let aria = "";
  if (acc.accTitle) {
    head += `<title id="${prefix}-title">${escText(acc.accTitle)}</title>`;
    aria += ` aria-labelledby="${prefix}-title"`;
  }
  if (acc.accDescr) {
    head += `<desc id="${prefix}-desc">${escText(acc.accDescr)}</desc>`;
    aria += ` aria-describedby="${prefix}-desc"`;
  }
  const fit = options.responsive === false ? "" : "max-width:100%;height:auto;";
  content = rounded(content);
  let weights = content.includes("var(--_r)") ? `;--_r:${RADIUS}` : "";
  for (const [alias, name] of WEIGHTS) {
    if (content.includes(`var(${alias})`)) weights += `;${alias}:var(--pele-${name}-weight,bold)`;
  }
  return `<svg xmlns="http://www.w3.org/2000/svg" class="pele pele-${type}" viewBox="0 0 ${width} ${height}" width="${width}" height="${height}" style="${fit}${ROOT_STYLE}${weights}${esc(fontStyle(fontSize, options.fontFamily, options.fontFamilyMono))}" fill="var(--_fg)" role="graphics-document document" aria-roledescription="${type}"${aria}>` + head + content + "</svg>";
}

// ../pele/src/diagrams/common/caseless.ts
function wordAt(src, at, word) {
  for (let k = 0; k < word.length; k++) {
    const c = src.charCodeAt(at + k);
    const w = word.charCodeAt(k);
    if (c !== w && !(w >= 97 && w <= 122 && c === w - 32)) return false;
  }
  return true;
}

// ../pele/src/diagrams/common/tokens.ts
var tokens_exports = {};
__export(tokens_exports, {
  ACC_DESCR: () => ACC_DESCR,
  ACC_TITLE: () => ACC_TITLE,
  DIRECTIVE: () => DIRECTIVE,
  NEWLINE: () => NEWLINE,
  Reader: () => Reader2,
  SINGLE_LINE_COMMENT: () => SINGLE_LINE_COMMENT,
  STRING: () => STRING,
  TITLE: () => TITLE,
  WHITESPACE: () => WHITESPACE,
  YAML: () => YAML,
  accDescrValue: () => accDescrValue,
  accTitleValue: () => accTitleValue,
  keyword: () => keyword,
  stringValue: () => stringValue,
  titleValue: () => titleValue,
  tokenize: () => tokenize
});
var tables = /* @__PURE__ */ new WeakMap();
function prepare(types) {
  const every = types.map((_, k) => k);
  const starting = [];
  for (let c = 0; c < 128; c++) {
    const ch = String.fromCharCode(c);
    starting.push(
      every.filter((k) => {
        const { pattern, first } = types[k];
        return typeof pattern === "string" ? pattern[0] === ch : first === void 0 || first.includes(ch);
      })
    );
  }
  const table = { starting, every, longer: types.map((type) => type.longer?.map((alt) => types.indexOf(alt))) };
  tables.set(types, table);
  return table;
}
function tokenize(src, types, diagram) {
  const kinds = [];
  const starts = [];
  const ends = [];
  const dead = new Uint8Array(types.length);
  const { starting, every, longer } = tables.get(types) ?? prepare(types);
  const scanners = [];
  const n = src.length;
  const match = (k, p2) => {
    if (dead[k]) return -1;
    const type = types[k];
    if (type.match !== void 0) return type.match(src, p2);
    if (type.scanner !== void 0) return (scanners[k] ??= type.scanner())(src, p2);
    const pattern = type.pattern;
    if (typeof pattern === "string") return src.startsWith(pattern, p2) ? p2 + pattern.length : -1;
    pattern.lastIndex = p2;
    if (pattern.test(src)) return pattern.lastIndex;
    if (type.opener !== void 0) {
      type.opener.lastIndex = p2;
      if (type.opener.test(src)) dead[k] = 1;
    }
    return -1;
  };
  let p = 0;
  scan: while (p < n) {
    const c = src.charCodeAt(p);
    for (const k of c < 128 ? starting[c] : every) {
      let end = match(k, p);
      if (end <= p) continue;
      let kind = k;
      const alts = longer[k];
      if (alts !== void 0) {
        for (const alt of alts) {
          const altEnd = match(alt, p);
          if (altEnd > end) {
            kind = alt;
            end = altEnd;
            break;
          }
        }
      }
      if (!types[kind].hidden) {
        kinds.push(kind);
        starts.push(p);
        ends.push(end);
      }
      p = end;
      continue scan;
    }
    throw syntaxError(diagram, src, p, "", true);
  }
  kinds.push(-1);
  starts.push(n);
  ends.push(n);
  return { kinds, starts, ends, src };
}
var ACC_DESCR = {
  name: "ACC_DESCR",
  pattern: /[\t ]*accDescr(?:[\t ]*:([^\n\r]*?(?=%%)|[^\n\r]*)|\s*{([^}]*)})/y,
  first: "	 a"
};
var ACC_TITLE = {
  name: "ACC_TITLE",
  pattern: /[\t ]*accTitle[\t ]*:(?:[^\n\r]*?(?=%%)|[^\n\r]*)/y,
  first: "	 a"
};
var TITLE = {
  name: "TITLE",
  pattern: /[\t ]*title(?:[\t ][^\n\r]*?(?=%%)|[\t ][^\n\r]*|)/y,
  first: "	 t"
};
var STRING = { name: "STRING", pattern: /"([^"\\]|\\.)*"|'([^'\\]|\\.)*'/y, first: `"'` };
var NEWLINE = { name: "NEWLINE", pattern: /\r?\n/y, first: "\r\n" };
var WHITESPACE = { name: "WHITESPACE", pattern: /[\t ]+/y, hidden: true, first: "	 " };
var YAML = {
  name: "YAML",
  pattern: /---[\t ]*\r?\n(?:[\S\s]*?\r?\n)?---(?:\r?\n|(?!\S))/y,
  hidden: true,
  opener: /---[\t ]*\r?\n/y,
  first: "-"
};
var DIRECTIVE = {
  name: "DIRECTIVE",
  pattern: /[\t ]*%%{[\S\s]*?}%%(?:\r?\n|(?!\S))/y,
  hidden: true,
  opener: /[\t ]*%%{/y,
  first: "	 %"
};
var SINGLE_LINE_COMMENT = {
  name: "SINGLE_LINE_COMMENT",
  pattern: /[\t ]*%%[^\n\r]*/y,
  first: "	 %",
  hidden: true
};
function keyword(word) {
  return {
    name: word,
    pattern: new RegExp(word.replace(/[.*+?^${}()|[\]\\]/g, "\\$&") + "(?:(?=%%)|(?!\\S))", "y"),
    first: word[0]
  };
}
var RE_ACC_DESCR = /accDescr(?:[\t ]*:([^\n\r]*)|\s*{([^}]*)})/;
var RE_ACC_TITLE = /accTitle[\t ]*:([^\n\r]*)/;
var RE_TITLE = /title([\t ][^\n\r]*|)/;
function single(text) {
  return text.trim().replace(/[\t ]{2,}/gm, " ");
}
function titleValue(text) {
  return single(RE_TITLE.exec(text)[1]);
}
function accTitleValue(text) {
  return single(RE_ACC_TITLE.exec(text)[1]);
}
function accDescrValue(text) {
  const m = RE_ACC_DESCR.exec(text);
  if (m[1] !== void 0) return single(m[1]);
  return m[2].replace(/^\s*/gm, "").replace(/\s+$/gm, "").replace(/[\t ]{2,}/gm, " ").replace(/[\n\r]{2,}/gm, "\n");
}
var ESCAPES2 = /* @__PURE__ */ new Map([
  ["b", "\b"],
  ["f", "\f"],
  ["n", "\n"],
  ["r", "\r"],
  ["t", "	"],
  ["v", "\v"],
  ["0", "\0"]
]);
function stringValue(text) {
  if (!text.includes("\\")) return text.slice(1, -1);
  let out = "";
  for (let i = 1; i < text.length - 1; i++) {
    const c = text[i];
    if (c === "\\") {
      const next = text[++i];
      out += ESCAPES2.get(next) ?? next;
    } else {
      out += c;
    }
  }
  return out;
}
var Reader2 = class {
  constructor(tokens, types, diagram) {
    this.tokens = tokens;
    this.types = types;
    this.diagram = diagram;
    this.kinds = tokens.kinds;
  }
  tokens;
  types;
  diagram;
  i = 0;
  kinds;
  get kind() {
    return this.kinds[this.i];
  }
  text(index = this.i) {
    return this.tokens.src.slice(this.tokens.starts[index], this.tokens.ends[index]);
  }
  take() {
    return this.text(this.i++);
  }
  accept(kind) {
    if (this.kinds[this.i] !== kind) return false;
    this.i++;
    return true;
  }
  expect(kind) {
    if (this.kinds[this.i] !== kind) this.fail(`token of type '${this.types[kind].name}'`);
    return this.take();
  }
  fail(expected) {
    const found2 = this.kind === -1 ? "end of input" : `\`${this.text()}\``;
    throw syntaxError(this.diagram, this.tokens.src, this.tokens.starts[this.i], `Expecting ${expected} but found ${found2}.`);
  }
};

// ../pele/src/diagrams/common/fit-width.ts
function fitWidth(options, natural, least, draw) {
  let result = draw(natural, false);
  const available = options.maxWidth;
  if (available === void 0 || !(available > 0) || result.width <= available) return result;
  const floor = Math.min(least, natural);
  let plot = natural;
  result = draw(plot, true);
  for (let round = 0; round < 3 && result.width > available && plot > floor; round++) {
    plot = Math.max(floor, plot - (result.width - available));
    result = draw(plot, true);
  }
  return result;
}
function titleRoom(options) {
  const available = options.maxWidth;
  return available !== void 0 && available > 0 ? Math.max(80, available - 2 * (options.padding ?? 8)) : 4e3;
}
function capWidth(options, natural, least, around = 0) {
  const available = options.maxWidth;
  if (available === void 0 || !(available > 0)) return natural;
  return Math.max(Math.min(least, natural), Math.min(natural, available - around));
}
export {
  CRITICAL,
  PeleError,
  RADIUS,
  ROOT_STYLE,
  capWidth,
  classNames,
  cleanupComments,
  decodeEntities,
  detect,
  encodeEntities,
  esc,
  escText,
  fitWidth,
  fontStyle,
  labelSvg,
  layoutLabel,
  locate,
  metricsMeasurer,
  num,
  parseYaml,
  preprocess,
  resolveStyle,
  seriesColor,
  sharedStyle,
  spanStyle,
  svgDocument,
  syntaxError,
  textStyle,
  titleRoom,
  tokens_exports as tokens,
  wordAt
};
