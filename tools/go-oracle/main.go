// Command go-oracle produces the reference data for OasDelta's emitter differential tests
// (tests/OasDelta.Tests/EmitterOracleTests.cs).
//
// oasdiff 1.35.0 writes YAML with go.yaml.in/yaml/v3 v3.0.5 (yaml.Marshal, indent 4) and JSON with encoding/json
// (json.Marshal). OasDelta reimplements both in src/OasDelta/Yaml/Emit.cs. This program
//
//  1. generates a deterministic corpus of values (hand-picked edge cases plus pseudo-random strings, maps and
//     numbers from a fixed seed) and writes it to corpus.json, then
//  2. reads corpus.json back, wraps each value in a few small documents (see docs) and writes what yaml.Marshal
//     and json.Marshal produce for them to expected.json.
//
// Both files are checked in, so the C# test runs without Go; CI reruns this program and fails if either file
// changes. Both files are ASCII: every non-ASCII or control character is written as a \uXXXX escape.
//
// Usage (from this directory): go run .            (writes ../../tests/emitter-oracle/{corpus,expected}.json)
//
//	go run . -dir <dir>
package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"math"
	"math/rand/v2"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"unicode/utf16"
	"unicode/utf8"

	"go.yaml.in/yaml/v3"
)

func main() {
	dir := flag.String("dir", filepath.Join("..", "..", "tests", "emitter-oracle"), "output directory")
	flag.Parse()

	corpus := appendCorpus(buildCorpus())
	if err := os.MkdirAll(*dir, 0o755); err != nil {
		fatal(err)
	}
	if err := os.WriteFile(filepath.Join(*dir, "corpus.json"), corpus, 0o644); err != nil {
		fatal(err)
	}
	expected, n := appendExpected(readCorpus(corpus))
	if err := os.WriteFile(filepath.Join(*dir, "expected.json"), expected, 0o644); err != nil {
		fatal(err)
	}
	fmt.Fprintf(os.Stderr, "go-oracle: %d corpus entries written to %s\n", n, *dir)
}

func fatal(err error) {
	fmt.Fprintln(os.Stderr, "go-oracle:", err)
	os.Exit(1)
}

// ---------------------------------------------------------------- value model

// pairs is a map whose generation order is kept in corpus.json, so the C# side inserts the keys in an arbitrary
// order and has to sort them the way the Go encoders do.
type pairs []kv

type kv struct {
	k string
	v any
}

type entry struct {
	id string
	v  any // string, int64, uint64, float64, bool, nil, []any, pairs
}

// docs wraps a corpus value in the documents that are marshalled. Together they put the value at the root and
// nested levels of block mappings, as the first and a later key of a mapping inside a sequence item, inside nested
// sequences, and (for strings) as mapping keys, so indentation, simple-key and literal-block rules are exercised.
func docs(v any) []any {
	d := []any{
		map[string]any{"m": map[string]any{"l": []any{v, map[string]any{"k": v, "x": v}, []any{v}}, "w": v}, "v": v},
		[]any{v, map[string]any{"k": v}},
	}
	if s, ok := v.(string); ok {
		d = append(d, map[string]any{s: map[string]any{s: []any{map[string]any{s: s}}}})
	}
	return d
}

// toGo converts the corpus model into the plain Go values the encoders see.
func toGo(v any) any {
	switch x := v.(type) {
	case pairs:
		m := make(map[string]any, len(x))
		for _, p := range x {
			m[p.k] = toGo(p.v)
		}
		return m
	case []any:
		l := make([]any, len(x))
		for i, e := range x {
			l[i] = toGo(e)
		}
		return l
	}
	return v
}

// ---------------------------------------------------------------- corpus.json

func appendCorpus(entries []entry) []byte {
	var b []byte
	b = append(b, "[\n"...)
	for i, e := range entries {
		b = append(b, `{"id":`...)
		b = appendString(b, e.id)
		b = append(b, `,"v":`...)
		b = appendTyped(b, e.v)
		b = append(b, '}')
		if i < len(entries)-1 {
			b = append(b, ',')
		}
		b = append(b, '\n')
	}
	return append(b, "]\n"...)
}

// appendTyped writes a corpus value: JSON strings, booleans and null stand for themselves; numbers, sequences and
// maps are objects with a single member ("int", "uint", "float", "seq", "map") so that their Go type is explicit.
func appendTyped(b []byte, v any) []byte {
	switch x := v.(type) {
	case nil:
		return append(b, "null"...)
	case bool:
		return strconv.AppendBool(b, x)
	case string:
		return appendString(b, x)
	case int64:
		return append(strconv.AppendInt(append(b, `{"int":"`...), x, 10), `"}`...)
	case uint64:
		return append(strconv.AppendUint(append(b, `{"uint":"`...), x, 10), `"}`...)
	case float64:
		return append(strconv.AppendFloat(append(b, `{"float":"`...), x, 'g', -1, 64), `"}`...)
	case []any:
		b = append(b, `{"seq":[`...)
		for i, e := range x {
			if i > 0 {
				b = append(b, ',')
			}
			b = appendTyped(b, e)
		}
		return append(b, "]}"...)
	case pairs:
		b = append(b, `{"map":[`...)
		for i, p := range x {
			if i > 0 {
				b = append(b, ',')
			}
			b = append(b, '[')
			b = appendString(b, p.k)
			b = append(b, ',')
			b = appendTyped(b, p.v)
			b = append(b, ']')
		}
		return append(b, "]}"...)
	}
	panic(fmt.Sprintf("unsupported corpus value %T", v))
}

// appendString writes s as an ASCII-only JSON string.
func appendString(b []byte, s string) []byte {
	if !utf8.ValidString(s) {
		panic(fmt.Sprintf("corpus string is not valid UTF-8: %q", s))
	}
	b = append(b, '"')
	for _, r := range s {
		switch {
		case r == '"':
			b = append(b, `\"`...)
		case r == '\\':
			b = append(b, `\\`...)
		case r == '\n':
			b = append(b, `\n`...)
		case r == '\r':
			b = append(b, `\r`...)
		case r == '\t':
			b = append(b, `\t`...)
		case r < 0x20 || r >= 0x7F:
			if r > 0xFFFF {
				r1, r2 := utf16.EncodeRune(r)
				b = fmt.Appendf(b, `\u%04x\u%04x`, r1, r2)
			} else {
				b = fmt.Appendf(b, `\u%04x`, r)
			}
		default:
			b = append(b, byte(r))
		}
	}
	return append(b, '"')
}

func readCorpus(data []byte) []entry {
	var raw []struct {
		ID string `json:"id"`
		V  any    `json:"v"`
	}
	if err := json.Unmarshal(data, &raw); err != nil {
		fatal(err)
	}
	entries := make([]entry, len(raw))
	for i, r := range raw {
		entries[i] = entry{r.ID, fromTyped(r.V)}
	}
	return entries
}

func fromTyped(v any) any {
	switch x := v.(type) {
	case nil, bool, string:
		return x
	case map[string]any:
		if len(x) != 1 {
			panic(fmt.Sprintf("bad typed value %v", x))
		}
		for k, e := range x {
			switch k {
			case "int":
				return must(strconv.ParseInt(e.(string), 10, 64))
			case "uint":
				return must(strconv.ParseUint(e.(string), 10, 64))
			case "float":
				return must(strconv.ParseFloat(e.(string), 64))
			case "seq":
				l := []any{}
				for _, i := range e.([]any) {
					l = append(l, fromTyped(i))
				}
				return l
			case "map":
				m := pairs{}
				for _, p := range e.([]any) {
					pp := p.([]any)
					m = append(m, kv{pp[0].(string), fromTyped(pp[1])})
				}
				return m
			}
		}
	}
	panic(fmt.Sprintf("bad typed value %v", v))
}

func must[T any](v T, err error) T {
	if err != nil {
		panic(err)
	}
	return v
}

// ---------------------------------------------------------------- expected.json

// marshalRuns is how often each document is marshalled. Go randomises map iteration, so if the yaml.v3 key
// comparator were not a strict weak order for some key set, the output would vary between runs; such a key set
// cannot be part of a byte-for-byte oracle, and this check catches it.
const marshalRuns = 5

func appendExpected(entries []entry) ([]byte, int) {
	var b []byte
	b = append(b, "[\n"...)
	for i, e := range entries {
		b = append(b, `{"id":`...)
		b = appendString(b, e.id)
		var ys, js []string
		for _, d := range docs(toGo(e.v)) {
			ys = append(ys, stable(e.id, func() (string, error) {
				out, err := yaml.Marshal(d)
				return string(out), err
			}))
			js = append(js, stable(e.id, func() (string, error) {
				out, err := json.Marshal(d)
				return string(out), err
			}))
		}
		b = append(b, `,"yaml":`...)
		b = appendStrings(b, ys)
		b = append(b, `,"json":`...)
		b = appendStrings(b, js)
		b = append(b, '}')
		if i < len(entries)-1 {
			b = append(b, ',')
		}
		b = append(b, '\n')
	}
	return append(b, "]\n"...), len(entries)
}

func stable(id string, f func() (string, error)) string {
	first, err := f()
	if err != nil {
		fatal(fmt.Errorf("%s: %w", id, err))
	}
	for range marshalRuns - 1 {
		if again, _ := f(); again != first {
			fatal(fmt.Errorf("%s: output depends on map iteration order:\n%s\n---\n%s", id, first, again))
		}
	}
	return first
}

func appendStrings(b []byte, l []string) []byte {
	b = append(b, '[')
	for i, s := range l {
		if i > 0 {
			b = append(b, ',')
		}
		b = appendString(b, s)
	}
	return append(b, ']')
}

// ---------------------------------------------------------------- corpus

type builder struct {
	entries []entry
	counts  map[string]int
	seen    map[string]bool
}

func (c *builder) add(cat string, v any) {
	if s, ok := v.(string); ok {
		if c.seen[s] {
			return
		}
		c.seen[s] = true
	}
	n := c.counts[cat]
	c.counts[cat]++
	c.entries = append(c.entries, entry{fmt.Sprintf("%s/%04d", cat, n), v})
}

func (c *builder) strs(cat string, l ...string) {
	for _, s := range l {
		c.add(cat, s)
	}
}

// interesting code points: every C0 and C1 control (NEL included), DEL, the edges of go-yaml's is_printable ranges
// (#xA0, #xD7FF, #xE000, #xFFFD), the BOM, the noncharacters U+FFFE/U+FFFF, the line and paragraph separators,
// format and combining characters, letters and digits outside ASCII, and characters beyond the BMP.
func codePoints() []rune {
	var l []rune
	for r := rune(0); r < 0x20; r++ {
		l = append(l, r)
	}
	l = append(l, 0x7F)
	for r := rune(0x80); r <= 0x9F; r++ {
		l = append(l, r)
	}
	return append(l,
		0xA0, 0xA1, 0xAD, 0xBF, 0xC0, 0xE9, 0xFF, 0x100, 0x300, 0x5D0, 0x661, 0x7FF, 0x800, 0x1680, 0x200B, 0x200D,
		0x200E, 0x2028, 0x2029, 0x202E, 0x2060, 0x3000, 0x4E2D, 0xCFFF, 0xD000, 0xD7FF, 0xE000, 0xEFFF, 0xF000,
		0xFDD0, 0xFEFF, 0xFFFC, 0xFFFD, 0xFFFE, 0xFFFF, 0x10000, 0x1D11E, 0x1F600, 0x1F680, 0xE0001, 0xF0000, 0x10FFFF)
}

const indicators = "-?:,[]{}#&*!|>'\"%@`"

func buildCorpus() []entry {
	c := &builder{counts: map[string]int{}, seen: map[string]bool{}}

	// ---- Unicode classes, each alone, inside, leading, trailing and in multi-line text
	for _, r := range codePoints() {
		x := string(r)
		c.strs("unicode", x, "a"+x+"b", x+"a", "a"+x, "line one\n"+x+" line two", x+x)
	}
	c.strs("unicode",
		"\U0001F468\u200D\U0001F469\u200D\U0001F467", "\U0001F1FA\U0001F1F8", "e\u0301", "\u00e9", "caf\u00e9 \U0001F600",
		"\ufeffabc", "\ufeff a", "\ufeff\nabc", "abc\ufeff", "a\ufeffb\nc", "\ufeff\ufeff", "\u00a0a\u00a0",
		"a\u0085\nb", "a\u2028\nb", "a\nb\u2028", "a\u2028b\nc", "\u2028a\nb", "a\nb\u2029", "a\u2029", "\u2028",
		"\u2028\u2028", "a\n\u2028", "a\u2028\n", "\u0085", "a\u0085", "\u0085a\nb", "\U0001F600\n\U0001F600",
		"\u4e2d\u6587", "\u05e9\u05dc\u05d5\u05dd", "\u0661\u0662\u0663", "\uff10\uff11", "\u00bd", "\u2160")

	// ---- tabs, spaces, line breaks
	c.strs("whitespace",
		"\t", "\t\t", "\ta", "a\t", "a\tb", "\ta\t", "a\n\tb", "\ta\nb", "a\nb\t", "a\t\nb", "\t\n", "\n\t", "a\n\t\nb",
		" ", "  ", " a", "a ", " a ", "a  b", "a \tb", "\t a", " \t", "a\t ",
		"\n", "\n\n", "\n\n\n", "a\n", "a\n\n", "a\n\n\n", "\na", "\n\na", "\na\n", "\n\na\n\n",
		"a\nb", "a\nb\n", "a\nb\n\n", "a\n\nb", "a\n\n\nb", " a\nb", "  a\n b", " \na", "a \nb", "a\n b", "a\n  b\n",
		"a\nb ", "a\nb\n ", " \n", "\n ", "a\n\n b", "a\n \nb", "a\n\t b", " a\n", "a\n ", "\n a", "\n\n a",
		"a\r\nb", "a\r\nb\r\n", "\r", "\r\n", "a\rb", "a\r", "\ra", "a\n\r\nb", "a\r\n", "\r\na",
		"line 1\nline 2\nline 3", "key: value\nother: value\n", "- a\n- b\n", "# comment\ntext", "  indented\nnot",
		"x\n\n\n\ny\n\n", "trailing spaces  \nnext", "a\n\n", "a\nb\n\n\n")

	// ---- YAML indicators at the start, middle and end
	for _, r := range indicators {
		x := string(r)
		c.strs("indicator",
			x, x+"a", x+" a", x+"\ta", "a"+x, "a"+x+"b", "a "+x, "a "+x+" b", "a"+x+" b", "a "+x+"b", x+x, x+" ",
			x+"\nb", "a\n"+x, x+"a\nb")
	}
	c.strs("indicator",
		"---", "...", "---a", "...a", "--- a", "... a", "----", "--", "..", "-- -", "---\n", "a\n---\nb", "a\n...\n",
		" ---", ".", "-.", "-a", "- a", "-\ta", "?\ta", ":\ta", "a:\tb", "a\t#b", "a #b", "a\t#", "#a", "a#",
		"http://example.com/a?b=c&d#e", "a: b", "a:b", "a:", "a::b", "::", ":", "- ", "? ", "?x", "?", "&anchor",
		"*alias", "!tag", "!!str", "!!binary", "%TAG", "%YAML 1.2", "@x", "`x`", "'x'", "\"x\"", "x'y", "it's",
		"'", "''", "\"", "a\"b", "a\\b", "\\", "\\n", "{a: b}", "[a, b]", "a, b", "a,b", "a[0]", "{}", "[]",
		"<<", "<< ", "=", "~", "~a", "a~", "a ~", "|", "|-", ">-", "| a", "> a", "a | b", "a > b", "#", "# a",
		"a # b", "a#b", "a #", "/api/v1/{id}", "/api/v1/{id}:", "$ref", "#/components/schemas/a", "application/json",
		"text/*; q=0.5", "x-extension", "a&b", "a<b>c", "<script>", "1 < 2 && 3 > 2", "AT&T")

	// ---- scalars that resolve to something other than a string
	c.strs("resolve",
		"", "true", "True", "TRUE", "tRue", "false", "False", "FALSE", "yes", "Yes", "YES", "yEs", "no", "No", "NO",
		"on", "On", "ON", "off", "Off", "OFF", "y", "Y", "n", "N", "null", "Null", "NULL", "nUll", "~", "nil", "None",
		"0", "1", "-1", "+1", "01", "07", "08", "09", "0777", "0o17", "0O17", "0o8", "0o", "-0o17", "+0o17",
		"0x1F", "0X1f", "0x", "0xg", "-0x1F", "+0x1F", "0b101", "0B1", "0b2", "0b", "-0b101", "+0b1",
		"1_000", "1__0", "_1", "1_", "0x_1F", "0_7", "-0_7", "0b_1", "1_0.5", "1e1_0", "_", "__",
		"1.5", "-1.5", ".5", "-.5", "+.5", "5.", "1e3", "1E3", "1e+3", "1e-3", "-1e3", ".5e3", "1.5e", "e3", "E3",
		"1.5.6", "1,000", "1 000", "1.0", "0.1", "1e10", "0.0", "-0", "+0", "00", "0.", "0e0", "-0.0", "1.e5",
		"9223372036854775807", "9223372036854775808", "-9223372036854775808", "-9223372036854775809",
		"18446744073709551615", "18446744073709551616", "0xFFFFFFFFFFFFFFFF", "0x10000000000000000",
		"1e308", "1e309", "1e400", "-1e400", "4.9e-324", "1e-400", "123456789012345678901234567890",
		".inf", ".Inf", ".INF", ".iNf", "-.inf", "+.inf", "-.Inf", "+.INF", ".nan", ".NaN", ".NAN", ".nAn",
		"inf", "Inf", "-inf", "nan", "NaN", "Infinity", "-Infinity", "infinity", ".infinity", "+inf",
		"0x1p-2", "0x1.8p1", "0x1P2", "1e", "1e+", "+", "-", "+-1", "--1", "1-", "1..2", "..5", ".5.", ".e5",
		"1:20", "1:20:30", "-1:20", "+1:20", "190:20:30.15", "1:60", "1:5:3", "1:2.5", "1_0:20", "12:30",
		"12:30:45", "0:0", "00:00:00", "1:00:00.5", "1:", ":1", "1::2", "a:1", "1:a", "1:2:", "1:2.", "1:2.3.4",
		"1:2_3", "1:23:45_6", "-.", "+.", "1:20 ", " 1:20")
	c.strs("timestamp",
		"2001-12-14", "2002-12-14", "2001-1-1", "2001-12-14t21:59:43.10-05:00", "2001-12-14T21:59:43.10-05:00",
		"2001-12-14 21:59:43.10", "2001-12-14T21:59:43Z", "2001-12-14T21:59:43.123456789Z",
		"2001-12-14T21:59:43", "2001-12-14 21:59:43.10 -5", "20011214", "2001-12-14x", "2001-02-30",
		"2001-13-01", "2001-00-10", "2001-1-0", "2000-02-29", "1900-02-29", "2001-12-14T24:00:00Z",
		"2001-12-14T23:60:00Z", "2001-12-14T23:59:60Z", "2001-12-14T1:2:3Z", "2001-12-14T21:59:43+25:00",
		"2001-12-14T21:59:43+24:00", "2001-12-14T21:59:43+05:61", "2001-12-14T21:59:43+05:60",
		"2001-12-14T21:59:43,5Z", "2001-12-14T21:59:43.1234567891Z", "2001-12-14 21:59:43.", "2001-12-14t21:59:43Z",
		"2001-12-14T21:59:43z", "0000-01-01", "2001-12-14T21:59Z", "2001-12-14 9:5:3", "2001-012-14",
		"2001-12-014", "02001-12-14", "2001-12-14T21:59:43.10-0500", "2001-12-14 21:59:43,10", "2001-12-14 ",
		" 2001-12-14", "2001-12-14\n", "2001-12-14 21:59", "2001-12-14T21:59:43+05", "2001-12-14T21:59:43-05:00:00",
		"2001-12-14T21:59:43.Z", "2001-12-14 21:59:43.123456789123", "2001-1-14", "2001-12-1", "201-12-14",
		"2001/12/14", "2001-12", "2001-", "2001-12-14T", "2001-12-14t", "2001-12-14 21:59:43Z", "+2001-12-14",
		"-2001-12-14", "2001-12-14T21:59:43.5+00:00", "9999-12-31", "2001-04-31", "2004-02-29", "2100-02-29",
		"2001-12-14T00:00:00-00:00", "2001-12-14T21:59:43+5:00")

	// ---- long strings and keys around go-yaml's 128-byte simple-key limit
	c.strs("long",
		strings.Repeat("a", 127), strings.Repeat("a", 128), strings.Repeat("a", 129), strings.Repeat("a", 300),
		strings.Repeat("\u00e9", 64), strings.Repeat("\u00e9", 65), strings.Repeat("a", 127)+"\u00e9",
		strings.Repeat("\U0001F600", 33), strings.Repeat("word ", 40)+"end", strings.Repeat("x", 1000),
		"/api/v1/"+strings.Repeat("segment/", 16)+"{id}", strings.Repeat("line of text\n", 12),
		strings.Repeat("a b ", 50), strings.Repeat("a", 126)+": b", strings.Repeat("a", 129)+"\nb",
		strings.Repeat("a\t", 70), strings.Repeat("\"", 130), strings.Repeat("a", 125)+"\u2028")

	// ---- multi-line text and chomping
	c.strs("multiline",
		"first\nsecond", "first\nsecond\n", "first\nsecond\n\n", "first\nsecond\n\n\n", "\nfirst", "\n\nfirst\n",
		" first\nsecond", "  first\n  second\n", "first\n second\n", "first\n\n\nsecond", "#a\nb", "- a\n- b",
		"a: b\nc: d", "{\n}", "[\n]", "'\nx", "\"\nx", "|\nx", ">\nx", "a\n---\n", "---\na", "...\na", "%\na",
		"!\na", "&a\nb", "*a\nb", "@a\nb", "`a\nb", "a\n#b", "a\n- b", "a\n? b", "a\n: b", "\u00e9\n\u00e9",
		"\U0001F600\nx", "x\n\U0001F600", "\u00a0\nx", "x\n\u00a0", "\x7f\nx", "x\n\x00", "a\n\u0085",
		"\u2029\na", "SELECT *\nFROM t\nWHERE a = 'b'\n", "{\n  \"a\": 1\n}\n", "<p>\n  &amp;\n</p>")

	// ---- other value types
	for _, i := range []int64{0, 1, -1, 7, 10, 100, 1 << 31, -1 << 31, 1<<53 + 1, math.MaxInt64, math.MinInt64} {
		c.add("int", i)
	}
	for _, u := range []uint64{0, 1, math.MaxInt64 + 1, math.MaxUint64} {
		c.add("uint", u)
	}
	for _, f := range []float64{
		0, math.Copysign(0, -1), 1, -1, 1.5, 0.1, 0.2, 1.0 / 3, 2.5e-5, 1e-6, 1e-7, 9.99e-7, 1e6, 123456, 1234567,
		12345678.9, 1e20, 1e21, 1e22, -1e21, 1.5e300, 1e100, 3e-10, 5e-324, math.MaxFloat64, math.SmallestNonzeroFloat64,
		100, 1e15, 1e16, 123456789012345680, 0.000001, 0.0000012345, 1e-5, 1e-4, 999999, 9999999, 2.2250738585072014e-308,
	} {
		c.add("float", f)
	}
	c.add("other", true)
	c.add("other", false)
	c.add("other", nil)

	// ---- collections
	c.add("struct", []any{})
	c.add("struct", pairs{})
	c.add("struct", []any{[]any{}, pairs{}, "", nil})
	c.add("struct", []any{[]any{[]any{"deep"}}})
	c.add("struct", []any{[]any{int64(1), int64(2)}, []any{int64(3)}})
	c.add("struct", pairs{{"a", []any{}}, {"b", pairs{}}, {"c", nil}, {"d", ""}})
	c.add("struct", pairs{{"a", pairs{{"b", pairs{{"c", pairs{{"d", pairs{{"e", "f\ng"}}}}}}}}}})
	c.add("struct", []any{"a\nb", pairs{{"x", "a\nb"}, {"y", []any{"c\n"}}}, []any{"\n\nz"}})
	c.add("struct", []any{pairs{{"a", []any{pairs{{"b", []any{"c"}}}}}}})
	c.add("struct", pairs{{"a\nb", "c"}, {strings.Repeat("k", 130), pairs{{"x", "y"}}}, {"z", "\u0085"}})

	// ---- key ordering: yaml.v3 compares digit runs numerically, encoding/json compares bytes
	for _, keys := range [][]string{
		{"resource2", "resource10", "resource1", "resource20", "resource3"},
		{"a1", "a01", "a001", "a", "a10", "a2"},
		{"x9", "x10", "x09", "x009", "x"},
		{"Z", "a", "_", "1", "10", "2", "-", "B", ".", "~"},
		{"a2b", "a10b", "a2c", "a10a", "a2"},
		{"v1.10", "v1.9", "v1.9.1", "v1.10.0", "v2"},
		{"/p/{id}", "/p/{id2}", "/p/10", "/p/9", "/p", "/p/"},
		{"\u00e4", "z", "a", "\u00e9", "Z", "\u00c4"},
		{"\ue000", "\U0001F600", "\ufffd", "\uff61", "a", "\u4e2d"},
		{"1", "01", "001", "0", "00"},
		{"0", "00", "a0", "a00", "a000", "a0b"},
		{"\u0663", "3", "2", "\u0662", "a"},
		{"get", "post", "put", "delete", "patch", "head", "options", "trace"},
		{"200", "201", "400", "404", "4XX", "default", "2XX", "500"},
		{"", " ", "a b", "a", "ab"},
		{"x-b", "x-a", "x-10", "x-9", "X-A"},
		{"item99", "item100", "item099", "item0100"},
	} {
		c.add("sort", keyMap(keys))
	}

	addRandom(c)
	return c.entries
}

func keyMap(keys []string) pairs {
	m := pairs{}
	for i, k := range keys {
		m = append(m, kv{k, int64(i)})
	}
	return m
}

// ---------------------------------------------------------------- pseudo-random part (fixed seed)

var pools = []struct {
	weight int
	chars  []rune
}{
	{30, []rune("abcxyzABCXYZ")},
	{10, []rune("0123456789")},
	{10, []rune(" ")},
	{4, []rune("\t")},
	{6, []rune("\n")},
	{2, []rune("\r")},
	{12, []rune(indicators)},
	{5, []rune(".~_/\\+=<>;$^()")},
	{2, []rune("\x00\x01\x07\x08\x0b\x0c\x1b\x1f\x7f")},
	{2, []rune("\u0080\u0085\u009f")},
	{3, []rune("\u00a0\u00e9\u00ff\u0100")},
	{2, []rune("\u2028\u2029")},
	{2, []rune("\ufeff\ufffe\uffff\ufffd")},
	{2, []rune("\ud7ff\ue000")},
	{3, []rune("\u4e2d\u05d0\u0663")},
	{3, []rune("\U0001F600\U0001D11E\U0010FFFF")},
}

var prefixes = []string{"true", "no", "null", "1", "0x1", "1.5", "1e3", "2001-12-14", "1:20", "---", "...", "- ", "~", ".inf"}

func addRandom(c *builder) {
	rng := rand.New(rand.NewPCG(20261107, 10))
	total := 0
	for _, p := range pools {
		total += p.weight
	}
	randRune := func() rune {
		n := rng.IntN(total)
		for _, p := range pools {
			if n < p.weight {
				return p.chars[rng.IntN(len(p.chars))]
			}
			n -= p.weight
		}
		panic("unreachable")
	}
	randString := func(maxLen int) string {
		var sb strings.Builder
		if rng.IntN(8) == 0 {
			sb.WriteString(prefixes[rng.IntN(len(prefixes))])
		}
		for n := rng.IntN(maxLen + 1); n > 0; n-- {
			sb.WriteRune(randRune())
		}
		return sb.String()
	}

	for c.counts["random"] < 1200 { // duplicates are skipped, so loop until there are 1200 distinct strings
		c.add("random", randString(12+rng.IntN(3)*8))
	}

	// map keys: mostly identifier-like with digit runs (the realistic case), some with arbitrary characters
	keyChars := []rune("aabbcxyz0123456789-_./{}")
	for range 120 {
		m := pairs{}
		used := map[string]bool{}
		for n := 2 + rng.IntN(9); n > 0; n-- {
			var k string
			if rng.IntN(4) == 0 {
				k = randString(8)
			} else {
				var sb strings.Builder
				for l := 1 + rng.IntN(8); l > 0; l-- {
					sb.WriteRune(keyChars[rng.IntN(len(keyChars))])
				}
				k = sb.String()
			}
			if !used[k] {
				used[k] = true
				m = append(m, kv{k, randString(6)})
			}
		}
		c.add("randmap", m)
	}

	for range 150 {
		f := math.Float64frombits(rng.Uint64())
		if math.IsNaN(f) || math.IsInf(f, 0) {
			continue
		}
		c.add("randfloat", f)
	}
	for range 60 {
		// decimal magnitudes spread evenly, where 'f' and 'e' formatting switch
		f := (rng.Float64()*2 - 1) * math.Pow(10, float64(rng.IntN(50)-25))
		c.add("randfloat", f)
	}
	for range 50 {
		c.add("randint", int64(rng.Uint64())>>rng.IntN(64))
	}
}
