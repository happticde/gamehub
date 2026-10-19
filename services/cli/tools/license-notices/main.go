// SPDX-FileCopyrightText: 2026 jfk1987 and GameHub contributors
// SPDX-License-Identifier: AGPL-3.0-only
// Build notices from resolved local packages. No network lookups or application configuration.
package main

import (
	"bytes"
	"crypto/sha256"
	"encoding/json"
	"encoding/xml"
	"flag"
	"fmt"
	"io"
	"io/fs"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"runtime"
	"sort"
	"strings"
)

type notice struct {
	File string `json:"file"`
	SHA  string `json:"sha256"`
}
type record struct {
	ID           string   `json:"id"`
	License      string   `json:"license"`
	Source       string   `json:"source,omitempty"`
	MetadataOnly bool     `json:"metadataOnly,omitempty"`
	Notices      []notice `json:"notices"`
}
type bundle struct {
	records []record
	texts   map[string]string
}

var legalName = regexp.MustCompile(`(?i)^(licen[sc]e|notice|copying|copyright|authors)([._-].*)?$`)

func read(name string) []byte { data, err := os.ReadFile(name); must(err); return data }
func must(err error) {
	if err != nil {
		panic(err)
	}
}
func (b *bundle) text(r *record, name string, data []byte) {
	digest := fmt.Sprintf("%x", sha256.Sum256(data))
	for _, item := range r.Notices {
		if item.SHA == digest {
			return
		}
	}
	b.texts[digest] = string(data)
	r.Notices = append(r.Notices, notice{name, digest})
}
func (b *bundle) files(r *record, directory string) {
	must(filepath.WalkDir(directory, func(name string, entry fs.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if name != directory && (entry.Name() == "node_modules" || entry.Name() == ".git" || strings.HasPrefix(entry.Name(), ".env")) {
			if entry.IsDir() {
				return filepath.SkipDir
			}
			return nil
		}
		if entry.Type()&os.ModeSymlink != 0 || entry.IsDir() || !legalName.MatchString(entry.Name()) {
			return nil
		}
		info, err := entry.Info()
		if err != nil {
			return err
		}
		if info.Size() > 2_000_000 {
			return fmt.Errorf("oversized legal input: %s", name)
		}
		relative, err := filepath.Rel(directory, name)
		if err != nil {
			return err
		}
		b.text(r, filepath.ToSlash(relative), read(name))
		return nil
	}))
}
func infer(text string) string {
	text = strings.ToLower(text)
	mit, apache := strings.Contains(text, "permission is hereby granted, free of charge"), strings.Contains(text, "apache license")
	switch {
	case mit && apache:
		return "MIT AND Apache-2.0"
	case mit:
		return "MIT"
	case apache:
		return "Apache-2.0"
	case strings.Contains(text, "isc license"):
		return "ISC"
	case strings.Contains(text, "redistribution and use in source and binary forms"):
		if strings.Contains(text, "endorse") {
			return "BSD-3-Clause"
		}
		return "BSD-2-Clause"
	case strings.Contains(text, "public domain"):
		return "LicenseRef-Public-Domain"
	}
	return ""
}
func (b *bundle) add(r record, directory, metadata, legal string) {
	b.files(&r, directory)
	if r.License == "" || r.License == "file" {
		var original strings.Builder
		for _, n := range r.Notices {
			original.WriteString(b.texts[n.SHA])
		}
		r.License = infer(original.String())
	}
	if r.License == "" {
		panic("Unresolved license: " + r.ID)
	}
	// Exact upstream metadata is retained when a package omits a separate legal file.
	// The declared license's complete standard text must also be available locally.
	if len(r.Notices) == 0 {
		if metadata == "" {
			panic("Missing original notice: " + r.ID)
		}
		// Select one available option of a dual license, retaining the original declaration in metadata.
		for _, option := range strings.Split(r.License, " OR ") {
			option = strings.Trim(option, "() ")
			if _, err := os.Stat(filepath.Join(legal, "standard", option+".txt")); err == nil {
				r.License = option
				break
			}
		}
		canonical := filepath.Join(legal, "standard", r.License+".txt")
		if _, err := os.Stat(canonical); err != nil {
			panic("Missing license text for " + r.ID + ": " + r.License)
		}
		b.text(&r, "standard license: "+r.License, read(canonical))
		r.MetadataOnly = true
		b.text(&r, filepath.Base(metadata)+" (original license/author metadata)", read(metadata))
	}
	// Supplement URL-only declarations and the LGPL text omitted by native libvips packages.
	for _, id := range []string{"MIT", "Apache-2.0", "LGPL-3.0-or-later"} {
		if strings.Contains(r.License, id) {
			b.text(&r, "standard license: "+id, read(filepath.Join(legal, "standard", id+".txt")))
		}
	}
	b.records = append(b.records, r)
}

func (b *bundle) npm(directory, legal string, production bool) {
	var lock struct {
		Packages map[string]struct {
			Version, License, Resolved string
			Dev, Optional              bool
		}
	}
	must(json.Unmarshal(read(filepath.Join(directory, "package-lock.json")), &lock))
	names := make([]string, 0, len(lock.Packages))
	for name := range lock.Packages {
		names = append(names, name)
	}
	sort.Strings(names)
	for _, name := range names {
		p := lock.Packages[name]
		if name == "" || (production && p.Dev) {
			continue
		}
		// Package paths come only from npm's lockfile, never an arbitrary checkout walk.
		if !strings.HasPrefix(name, "node_modules/") || strings.Contains(name, "..") || strings.Contains(name, "\\") {
			panic("Invalid package path: " + name)
		}
		location := filepath.Join(directory, filepath.FromSlash(name))
		meta := filepath.Join(location, "package.json")
		if _, err := os.Stat(meta); err != nil {
			if os.IsNotExist(err) && p.Optional {
				continue
			}
			panic("Missing installed package: " + name)
		}
		var pkg struct {
			Name, Version string
			License       json.RawMessage
			Licenses      []struct{ Type string }
		}
		must(json.Unmarshal(read(meta), &pkg))
		if pkg.Version != p.Version {
			panic("Installed package differs from lockfile: " + name)
		}
		if p.License == "" {
			var expression string
			if len(pkg.License) > 0 {
				if err := json.Unmarshal(pkg.License, &expression); err != nil {
					var legacy struct{ Type string }
					must(json.Unmarshal(pkg.License, &legacy))
					expression = legacy.Type
				}
			}
			if expression == "" {
				for _, item := range pkg.Licenses {
					if expression != "" {
						expression += " OR "
					}
					expression += item.Type
				}
			}
			p.License = expression
		}
		r := record{ID: "npm:" + pkg.Name + "@" + p.Version, License: p.License, Source: p.Resolved}
		b.add(r, location, meta, legal)
		if strings.HasPrefix(pkg.Name, "@img/sharp-libvips-") {
			native := record{ID: pkg.Name + " native components", License: "See original component sources", Source: "https://github.com/lovell/sharp-libvips"}
			b.text(&native, "versions.json", read(filepath.Join(location, "versions.json")))
			b.records = append(b.records, native)
		}
	}
}
func (b *bundle) nuget(assets, legal string) {
	var data struct {
		Libraries      map[string]struct{ Type, Path string }
		PackageFolders map[string]json.RawMessage
	}
	must(json.Unmarshal(read(assets), &data))
	for id, lib := range data.Libraries {
		if lib.Type != "package" {
			continue
		}
		var directory string
		for folder := range data.PackageFolders {
			candidate := filepath.Join(folder, lib.Path)
			if info, err := os.Stat(candidate); err == nil && info.IsDir() {
				directory = candidate
				break
			}
		}
		if directory == "" {
			panic("Missing restored package: " + id)
		}
		files, err := filepath.Glob(filepath.Join(directory, "*.nuspec"))
		must(err)
		if len(files) != 1 {
			panic("Expected one nuspec: " + id)
		}
		var spec struct {
			Metadata struct {
				License struct {
					Type string `xml:"type,attr"`
					Text string `xml:",chardata"`
				} `xml:"license"`
				ProjectURL string `xml:"projectUrl"`
			} `xml:"metadata"`
		}
		must(xml.Unmarshal(read(files[0]), &spec))
		license := strings.TrimSpace(spec.Metadata.License.Text)
		if spec.Metadata.License.Type == "file" {
			license = "file"
		}
		b.add(record{ID: "nuget:" + id, License: license, Source: spec.Metadata.ProjectURL}, directory, files[0], legal)
	}
}
func (b *bundle) goModules(directory, legal string) {
	// Go's lazy module loading can leave graph members without source directories.
	// Resolve the locked module graph before collecting its original notice files.
	download := exec.Command("go", "mod", "download", "all")
	download.Dir = directory
	download.Stderr = os.Stderr
	must(download.Run())
	cmd := exec.Command("go", "list", "-m", "-json", "all")
	cmd.Dir = directory
	data, err := cmd.Output()
	must(err)
	decoder := json.NewDecoder(bytes.NewReader(data))
	for {
		var module struct {
			Path, Version, Dir string
			Main               bool
			Replace            *json.RawMessage
		}
		err := decoder.Decode(&module)
		if err == io.EOF {
			break
		}
		must(err)
		if module.Main {
			continue
		}
		if module.Replace != nil || module.Dir == "" {
			panic("Unresolved/replaced Go module: " + module.Path)
		}
		b.add(record{ID: "go:" + module.Path + "@" + module.Version, Source: "https://" + module.Path}, module.Dir, "", legal)
	}
	r := record{ID: "Go standard library " + runtime.Version(), License: "BSD-3-Clause", Source: "https://go.dev/dl/"}
	b.text(&r, "Go LICENSE", read(filepath.Join(runtime.GOROOT(), "LICENSE")))
	b.records = append(b.records, r)
}
func (b *bundle) write(output, legal, extra string) {
	if extra != "" {
		r := record{ID: "Copied frontend components, starter assets and fonts", License: "See original notices"}
		b.text(&r, "frontend originals", read(filepath.Join(legal, "notices", "frontend.txt")))
		b.records = append(b.records, r)
	}
	sort.Slice(b.records, func(i, j int) bool { return b.records[i].ID < b.records[j].ID })
	var text strings.Builder
	text.WriteString("Third-party notices for this GameHub build.\nOriginal works retain their own licenses; GameHub does not relicense them.\n")
	for _, r := range b.records {
		fmt.Fprintf(&text, "\n%s — %s\n", r.ID, r.License)
		if r.Source != "" {
			fmt.Fprintf(&text, "Source: %s\n", r.Source)
		}
		if r.MetadataOnly {
			text.WriteString("No separate notice shipped; original package metadata and declared license text follow.\n")
		}
		for _, n := range r.Notices {
			fmt.Fprintf(&text, "  %s: notice %s\n", n.File, n.SHA)
		}
	}
	keys := make([]string, 0, len(b.texts))
	for key := range b.texts {
		keys = append(keys, key)
	}
	sort.Strings(keys)
	for _, key := range keys {
		fmt.Fprintf(&text, "\n===== Notice %s =====\n%s\n", key, b.texts[key])
	}
	must(os.MkdirAll(output, 0755))
	must(os.WriteFile(filepath.Join(output, "LICENSE"), read(filepath.Join(legal, "LICENSE")), 0644))
	must(os.WriteFile(filepath.Join(output, "THIRD_PARTY_NOTICES.txt"), []byte(text.String()), 0644))
	inventory, err := json.MarshalIndent(struct {
		SchemaVersion int      `json:"schemaVersion"`
		Packages      []record `json:"packages"`
	}{1, b.records}, "", "  ")
	must(err)
	must(os.WriteFile(filepath.Join(output, "dependency-inventory.json"), append(inventory, '\n'), 0644))
	fmt.Printf("Generated %d package notices in %s\n", len(b.records), output)
}
func main() {
	kind := flag.String("kind", "", "npm, nuget or go")
	input := flag.String("input", ".", "npm/Go project directory or NuGet assets file")
	legal := flag.String("legal", "licensing", "original legal inputs")
	output := flag.String("output", "", "generated output directory")
	production := flag.Bool("production", false, "omit npm development dependencies")
	extra := flag.String("extra", "", "frontend for copied component/font notices")
	flag.Parse()
	if *output == "" {
		panic("An output directory is required")
	}
	b := bundle{texts: map[string]string{}}
	switch *kind {
	case "npm":
		b.npm(*input, *legal, *production)
	case "nuget":
		b.nuget(*input, *legal)
	case "go":
		b.goModules(*input, *legal)
	default:
		panic("Unknown package kind")
	}
	if len(b.records) == 0 {
		panic("No resolved packages; refusing to publish an empty notice bundle")
	}
	b.write(*output, *legal, *extra)
}
