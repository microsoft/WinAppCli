# Shell Completion

Enable tab completion for `winapp` in your terminal. Once activated, pressing Tab will suggest commands, options, and argument values as you type.

## PowerShell

Run the following to print the registration script:

```powershell
winapp complete --setup powershell
```

To activate, add the output to your PowerShell profile:

```powershell
winapp complete --setup powershell >> $PROFILE
```

Then restart PowerShell (or run `. $PROFILE` to reload).

To try it in the current session without modifying your profile:

```powershell
winapp complete --setup powershell | Out-String | Invoke-Expression
```

### What it does

Registers a native argument completer that calls `winapp complete` on each Tab press, providing context-aware suggestions for commands, subcommands, options, and values.

### To deactivate

Open your profile (`notepad $PROFILE`) and remove the `Register-ArgumentCompleter` block for `winapp`. Restart PowerShell.

## Bash

```bash
winapp complete --setup bash >> ~/.bashrc
source ~/.bashrc
```

### To deactivate

Remove the `_winapp_completions` function and the `complete -o default -F _winapp_completions winapp` line from `~/.bashrc`. Restart your shell.

## Zsh

```zsh
winapp complete --setup zsh >> ~/.zshrc
source ~/.zshrc
```

### To deactivate

Remove the `_winapp` function and `compdef _winapp winapp` line from `~/.zshrc`. Restart your shell.

## What gets completed

- **Commands**: `winapp i` + Tab → `init`
- **Subcommands**: `winapp cert ` + Tab → `generate`, `install`, `info`
- **Options**: `winapp init --` + Tab → `--setup-sdks`, `--config-dir`, `--use-defaults`, ...
- **Option values**: Enum-based options suggest valid values when available
- **Node.js wrapper commands**: `node`, `node create-addon`, etc. (when installed via npm)

Completions scale automatically — any new command or option added to the CLI is instantly completable with no additional setup.
