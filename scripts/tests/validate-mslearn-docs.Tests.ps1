#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }
<#
    Pester tests for the MS Learn docs gate (scripts/validate-mslearn-docs.ps1)
    and the shared front-matter rules (scripts/mslearn-doc-lib.ps1).

    The validator hard-gates the release porting job, so these tests pin its
    rules and exit codes: description bounds, banned words, YAML-unsafe values
    (including leading block indicators), the multi-paragraph alert callout
    behaviour, and the 0/1 exit contract.
#>

BeforeAll {
    $script:ScriptsDir = Split-Path $PSScriptRoot -Parent
    $script:Validator = Join-Path $ScriptsDir 'validate-mslearn-docs.ps1'
    $script:Lib = Join-Path $ScriptsDir 'mslearn-doc-lib.ps1'
    . $script:Lib

    function New-TempDocsRoot {
        $d = Join-Path ([System.IO.Path]::GetTempPath()) ("mslearn-val-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $d -Force | Out-Null
        return $d
    }

    # A safe description of exactly $Len chars: lowercase letters + spaces only,
    # never starting or ending with a space (which would itself be YAML-unsafe).
    function New-SafeDescription {
        param([int]$Len)
        $base = 'register temporary package identity so you can debug identity dependent windows features from build output '
        $s = ''
        while ($s.Length -lt $Len) { $s += $base }
        $s = $s.Substring(0, $Len)
        if ($s[0] -eq ' ') { $s = 'x' + $s.Substring(1) }
        if ($s[-1] -eq ' ') { $s = $s.Substring(0, $Len - 1) + 'x' }
        return $s
    }

    function New-DocContent {
        param(
            [string]$Title = 'Sample Page',
            [string]$Description = '',
            [string]$Body = 'Plain instructional body text.'
        )
        if ([string]::IsNullOrEmpty($Description)) { $Description = New-SafeDescription 130 }
        return @"
<!-- mslearn: true -->
<!-- description: $Description -->

# $Title

$Body
"@
    }

    function Invoke-Validator {
        param([string]$Root)
        $out = & pwsh -NoProfile -File $script:Validator -DocsRoot $Root *>&1 | Out-String
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $out }
    }

    function Set-Doc {
        param([string]$Root, [string]$Content, [string]$Name = 'doc.md')
        Set-Content -Path (Join-Path $Root $Name) -Value $Content -Encoding UTF8
    }
}

Describe 'mslearn-doc-lib: Test-MsLearnYamlUnsafe' {
    It 'treats a plain sentence as safe' {
        Test-MsLearnYamlUnsafe 'Register identity so you can debug features' | Should -BeFalse
    }
    It 'flags <_> as unsafe' -ForEach @('- leading dash', '? leading question', ': has colon', '#hash', '| pipe', '> gt', 'trailing space ', ' leading space', '~null', ', comma') {
        Test-MsLearnYamlUnsafe $_ | Should -BeTrue
    }
    It 'quotes only unsafe values' {
        Format-MsLearnYamlValue 'plain text' | Should -Be 'plain text'
        Format-MsLearnYamlValue '- dash' | Should -Be '"- dash"'
    }
}

Describe 'mslearn-doc-lib: description + title resolution' {
    It 'extracts the first H1 as the title' {
        Get-MsLearnTitle "# Hello World`n`nbody" | Should -Be 'Hello World'
    }
    It 'prefers the description marker over the title' {
        $r = Resolve-MsLearnDescription -Content '<!-- description: My desc -->' -Title 'T'
        $r.Description | Should -Be 'My desc'
        $r.HasMarker | Should -BeTrue
    }
    It 'defaults the description to the title when no marker exists' {
        $r = Resolve-MsLearnDescription -Content 'no marker here' -Title 'T'
        $r.Description | Should -Be 'T'
        $r.HasMarker | Should -BeFalse
    }
}

Describe 'validate-mslearn-docs gate' {
    BeforeEach { $script:Root = New-TempDocsRoot }
    AfterEach { Remove-Item $script:Root -Recurse -Force -ErrorAction SilentlyContinue }

    It 'passes a well-formed doc (exit 0)' {
        Set-Doc $Root (New-DocContent)
        $r = Invoke-Validator $Root
        $r.ExitCode | Should -Be 0
        $r.Output | Should -Not -Match '\[error\]'
    }

    It 'fails a too-short description (exit 1)' {
        Set-Doc $Root (New-DocContent -Description (New-SafeDescription 50))
        $r = Invoke-Validator $Root
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'description length'
    }

    It 'fails a too-long description (exit 1)' {
        Set-Doc $Root (New-DocContent -Description (New-SafeDescription 200))
        $r = Invoke-Validator $Root
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'description length'
    }

    It 'fails when the description marker is missing (exit 1)' {
        Set-Doc $Root "<!-- mslearn: true -->`n`n# Only A Title`n`nbody"
        $r = Invoke-Validator $Root
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'description is missing or duplicates'
    }

    It 'fails a YAML-unsafe description with a leading block indicator (exit 1)' {
        Set-Doc $Root (New-DocContent -Description ('- ' + (New-SafeDescription 128)))
        $r = Invoke-Validator $Root
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'quoted YAML value'
    }

    It 'fails on a banned marketing word and reports the line number (exit 1)' {
        Set-Doc $Root (New-DocContent -Body 'This is a powerful feature.')
        $r = Invoke-Validator $Root
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "banned marketing word 'powerful' on line"
    }

    It 'warns (non-fatal) on a bold blockquote callout without alert syntax' {
        Set-Doc $Root (New-DocContent -Body "> **Note:** a bold blockquote used as a callout.")
        $r = Invoke-Validator $Root
        $r.ExitCode | Should -Be 0
        $r.Output | Should -Match 'does not use MS Learn alert syntax'
    }

    It 'does NOT warn on a multi-paragraph alert whose block opened with a marker' {
        $body = @"
> [!NOTE]
> This is an important note about the feature.
>
> **Details:** additional explanation here.
"@
        Set-Doc $Root (New-DocContent -Body $body)
        $r = Invoke-Validator $Root
        $r.ExitCode | Should -Be 0
        $r.Output | Should -Not -Match 'does not use MS Learn alert syntax'
    }
}
