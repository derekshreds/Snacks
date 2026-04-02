@echo off
REM Creates a self-signed code signing certificate for signing the Snacks installer.
REM Run this once. The certificate is valid for 5 years.
REM The .pfx file is gitignored and should not be committed.

echo.
echo  Creating self-signed code signing certificate...
echo.

set /p CERT_PASS=Enter a password for the certificate:

if not exist "signing" mkdir signing

powershell -Command ^
    "$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=Derek Morris' -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5) -FriendlyName 'Snacks Code Signing'; ^
    Export-PfxCertificate -Cert ('Cert:\CurrentUser\My\' + $cert.Thumbprint) -FilePath 'signing\snacks-signing.pfx' -Password (ConvertTo-SecureString -String '%CERT_PASS%' -Force -AsPlainText) | Out-Null; ^
    Write-Host ('Certificate created: signing\snacks-signing.pfx'); ^
    Write-Host ('Thumbprint: ' + $cert.Thumbprint)"

echo %CERT_PASS%> signing\password.txt

echo.
echo  Done! Certificate and password saved to signing\ (gitignored).
echo.

pause
