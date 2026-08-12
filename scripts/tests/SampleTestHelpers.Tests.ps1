<#
.SYNOPSIS
Unit tests for samples/SampleTestHelpers.psm1.

.DESCRIPTION
Covers ConvertTo-ArgumentList, which splits the single -Arguments string that the
sample tests pass into the discrete arguments splatted onto the resolved executable.
A tokenizer defect here silently changes what every sample test executes, so it is
worth pinning down directly rather than only through the sample matrix.
#>

BeforeAll {
    $script:HelpersModule = Join-Path $PSScriptRoot "..\..\samples\SampleTestHelpers.psm1"
    Import-Module $script:HelpersModule -Force
}

AfterAll {
    Remove-Module SampleTestHelpers -Force -ErrorAction SilentlyContinue
}

Describe 'ConvertTo-ArgumentList' {

    Context 'Return shape' {
        It 'returns an array for a single token' {
            # Regression: PowerShell unrolls a one-element array on output, and splatting
            # the resulting scalar passes one character per argument.
            InModuleScope SampleTestHelpers {
                $result = ConvertTo-ArgumentList -Arguments 'restore'
                ($result -is [array]) | Should -BeTrue
                $result.Count | Should -Be 1
                $result[0] | Should -Be 'restore'
            }
        }

        It 'returns an empty array for empty or whitespace input' {
            InModuleScope SampleTestHelpers {
                foreach ($input in @('', '   ')) {
                    $result = ConvertTo-ArgumentList -Arguments $input
                    ($result -is [array]) | Should -BeTrue
                    $result.Count | Should -Be 0
                }
            }
        }

        It 'does not nest the array' {
            InModuleScope SampleTestHelpers {
                $result = ConvertTo-ArgumentList -Arguments 'cert generate'
                $result[0] | Should -BeOfType [string]
            }
        }
    }

    Context 'Splatting behavior' {
        It 'passes a single token as exactly one argument' {
            InModuleScope SampleTestHelpers {
                $result = ConvertTo-ArgumentList -Arguments 'restore'
                $probe = { $args.Count }
                (& $probe @result) | Should -Be 1
            }
        }

        It 'passes each token as its own argument' {
            InModuleScope SampleTestHelpers {
                $result = ConvertTo-ArgumentList -Arguments 'cert generate --if-exists skip'
                $probe = { $args -join '|' }
                (& $probe @result) | Should -Be 'cert|generate|--if-exists|skip'
            }
        }
    }

    Context 'Tokenizing' {
        It 'splits on whitespace' {
            InModuleScope SampleTestHelpers {
                $result = ConvertTo-ArgumentList -Arguments 'cert generate --if-exists skip'
                $result.Count | Should -Be 4
            }
        }

        It 'keeps a double-quoted value containing spaces as one argument' {
            InModuleScope SampleTestHelpers {
                $result = ConvertTo-ArgumentList -Arguments 'pack "C:\my path\out" --cert devcert.pfx'
                $result.Count | Should -Be 4
                $result[1] | Should -Be 'C:\my path\out'
            }
        }

        It 'keeps a single-quoted value containing spaces as one argument' {
            InModuleScope SampleTestHelpers {
                $result = ConvertTo-ArgumentList -Arguments "cert generate --publisher 'CN=Sparse Guide'"
                $result[-1] | Should -Be 'CN=Sparse Guide'
            }
        }

        It 'preserves an embedded equals sign' {
            InModuleScope SampleTestHelpers {
                $result = ConvertTo-ArgumentList -Arguments 'init . --use-defaults --setup-sdks=stable'
                $result[-1] | Should -Be '--setup-sdks=stable'
            }
        }

        It 'preserves backslashes in relative paths' {
            InModuleScope SampleTestHelpers {
                $result = ConvertTo-ArgumentList -Arguments 'run .\target\debug --with-alias'
                $result[1] | Should -Be '.\target\debug'
            }
        }

        It 'collapses runs of whitespace between tokens' {
            InModuleScope SampleTestHelpers {
                $result = ConvertTo-ArgumentList -Arguments 'cert    info   devcert.pfx'
                $result.Count | Should -Be 3
            }
        }
    }
}
