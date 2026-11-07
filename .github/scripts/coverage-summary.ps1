# Writes a Markdown coverage summary: overall line/branch coverage plus a table per namespace (OasDelta.<Area>),
# from ReportGenerator's JsonSummary, followed by the per-class tables of its MarkdownSummaryGithub report when given.
# Usage: pwsh coverage-summary.ps1 <Summary.json> [SummaryGithub.md]  (Markdown goes to stdout)
param([Parameter(Mandatory)][string]$SummaryJson, [string]$ClassesMarkdown)

$ErrorActionPreference = 'Stop'
$inv = [cultureinfo]::InvariantCulture
$summary = Get-Content -Raw $SummaryJson | ConvertFrom-Json

function Pct([double]$covered, [double]$total) {
    if ($total -eq 0) { return 'n/a' }
    # truncated to one decimal, as ReportGenerator does, so the numbers agree with its per-class table
    [string]::Format($inv, '{0:0.0}%', [math]::Floor(1000.0 * $covered / $total) / 10)
}

$s = $summary.summary
"## Code coverage"
""
"**Line coverage: $(Pct $s.coveredlines $s.coverablelines)** ($($s.coveredlines) of $($s.coverablelines) lines) · " +
"**Branch coverage: $(Pct $s.coveredbranches $s.totalbranches)** ($($s.coveredbranches) of $($s.totalbranches) branches)"
""
"Generated code (``*.g.cs``) is excluded. The full HTML report is in the ``coverage-report`` artifact."
""
"| Namespace | Line | Branch | Lines covered | Branches covered |"
"|:---|---:|---:|---:|---:|"

# Namespaces are two levels deep (OasDelta.Checker, OasDelta.Diff, ...); nested types share their parent's namespace.
$classes = $summary.coverage.assemblies | ForEach-Object { $_.classesinassembly }
$classes | Group-Object { ($_.name -split '\.')[0..1] -join '.' } | Sort-Object Name | ForEach-Object {
    $cl = ($_.Group | Measure-Object coveredlines -Sum).Sum
    $tl = ($_.Group | Measure-Object coverablelines -Sum).Sum
    $cb = ($_.Group | Measure-Object coveredbranches -Sum).Sum
    $tb = ($_.Group | Measure-Object totalbranches -Sum).Sum
    "| $($_.Name) | $(Pct $cl $tl) | $(Pct $cb $tb) | $cl / $tl | $cb / $tb |"
}

if ($ClassesMarkdown) {
    # ReportGenerator's report repeats the totals first; keep only its per-class section (collapsed by default).
    $lines = Get-Content $ClassesMarkdown
    $start = [array]::IndexOf($lines, '## Coverage')
    if ($start -ge 0) {
        ""
        "### Per class"
        $lines[($start + 1)..($lines.Count - 1)]
    }
}
