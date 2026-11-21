package main

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"unicode"
)

// writeCaseTable writes go-case.txt: for every rune that strings.ToLower or strings.ToUpper changes, or that
// strings.EqualFold matches to another rune, one line "RUNE LOWER UPPER ORBIT" in hex, where ORBIT is the smallest
// rune of its unicode.SimpleFold orbit. A rune that is not listed maps to itself and folds only to itself.
// tests/OasDelta.Tests/GoTextTests.cs checks src/OasDelta/GoText.cs against every rune of it.
func writeCaseTable(dir string) int {
	var b strings.Builder
	fmt.Fprintf(&b, "# unicode %s: rune, strings.ToLower, strings.ToUpper, smallest rune of the unicode.SimpleFold orbit\n", unicode.Version)
	n := 0
	for r := rune(0); r <= unicode.MaxRune; r++ {
		if r >= 0xD800 && r <= 0xDFFF {
			continue
		}
		s := string(r)
		lower, upper := []rune(strings.ToLower(s)), []rune(strings.ToUpper(s))
		if len(lower) != 1 || len(upper) != 1 {
			fatal(fmt.Errorf("rune %X: a case mapping that is not one rune", r))
		}
		orbit := r
		for f := unicode.SimpleFold(r); f != r; f = unicode.SimpleFold(f) {
			orbit = min(orbit, f)
		}
		if lower[0] == r && upper[0] == r && unicode.SimpleFold(r) == r {
			continue
		}
		fmt.Fprintf(&b, "%X %X %X %X\n", r, lower[0], upper[0], orbit)
		n++
	}
	if err := os.WriteFile(filepath.Join(dir, "go-case.txt"), []byte(b.String()), 0o644); err != nil {
		fatal(err)
	}
	return n
}
