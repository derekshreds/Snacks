!macro customInstall
  nsExec::Exec '"$SYSDIR\netsh.exe" advfirewall firewall delete rule name="Snacks - Distributed Encoding (TCP)"'
  Pop $0
  nsExec::Exec '"$SYSDIR\netsh.exe" advfirewall firewall delete rule name="Snacks - Distributed Encoding (UDP)"'
  Pop $0
  nsExec::Exec '"$SYSDIR\netsh.exe" advfirewall firewall add rule name="Snacks - Distributed Encoding (TCP)" dir=in action=allow protocol=TCP localport=6767 profile=any'
  Pop $0
  nsExec::Exec '"$SYSDIR\netsh.exe" advfirewall firewall add rule name="Snacks - Distributed Encoding (UDP)" dir=in action=allow protocol=UDP localport=6768 profile=any'
  Pop $0
!macroend

!macro customUnInstall
  nsExec::Exec '"$SYSDIR\netsh.exe" advfirewall firewall delete rule name="Snacks - Distributed Encoding (TCP)"'
  Pop $0
  nsExec::Exec '"$SYSDIR\netsh.exe" advfirewall firewall delete rule name="Snacks - Distributed Encoding (UDP)"'
  Pop $0
!macroend
