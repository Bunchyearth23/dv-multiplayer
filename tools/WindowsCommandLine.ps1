function ConvertTo-WindowsCommandLineArgument {
    param([AllowEmptyString()][string]$Value)

    # Windows argv quoting: double backslashes before quotes and the closing quote.
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}
