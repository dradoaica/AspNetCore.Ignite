openssl genrsa -out ca.key 2048
openssl req -x509 -new -nodes -key ca.key -out ca.crt -sha256 -days 3650 -config ca_key.cnf
keytool -deststorepass 123456 -noprompt -import -v -trustcacerts -file ca.crt -alias certificateauthority -keystore trust.jks

openssl genrsa -out server.key 2048
openssl req -key server.key -new -out server.csr -config server_key.cnf
openssl ca -batch -config ca.cnf -out server.crt -infiles server.csr
Get-Content server.key, server.crt, ca.crt | Set-Content server.pem
openssl pkcs12 -export -inkey server.key -in server.pem -name server -out server.pfx -passin pass:123456 -passout pass:123456
keytool -importkeystore -srckeystore server.pfx -srcstoretype pkcs12 -srcstorepass 123456 -destkeystore server.jks -deststoretype jks -deststorepass 123456

openssl genrsa -out client.key 2048
openssl req -key client.key -new -out client.csr -config client_key.cnf
openssl ca -batch -config ca.cnf -out client.crt -infiles client.csr
Get-Content client.key, client.crt, ca.crt | Set-Content client.pem
openssl pkcs12 -export -inkey client.key -in client.pem -name client -out thin-client-cert.pfx -passin pass:123456 -passout pass:123456
