# Copia os arquivos recebidos para a area de transferencia (Ctrl+V no Explorer cola)
if ($args.Count -gt 0) { Set-Clipboard -LiteralPath $args }
