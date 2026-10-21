import datetime as dt
import ipaddress
import socket
import ssl
import threading

from cryptography import x509
from cryptography.x509.oid import ExtendedKeyUsageOID

from phone_teleop import certs


def load(path):
    return x509.load_pem_x509_certificate(path.read_bytes())


def test_chain_verifies_and_constraints(tmp_path):
    hosts = ["192.168.1.23", "mylaptop.local", "localhost", "127.0.0.1"]
    cert_path, key_path = certs.ensure_server_cert(tmp_path, hosts)
    ca = load(tmp_path / certs.CA_CERT)
    leaf = load(cert_path)

    # cryptography's own path validation (X.509 / CABF-ish rules)
    from cryptography.x509.verification import PolicyBuilder, Store
    store = Store([ca])
    for host in ["mylaptop.local", "localhost"]:
        verifier = PolicyBuilder().store(store).build_server_verifier(x509.DNSName(host))
        verifier.verify(leaf, [])
    for ip in ["192.168.1.23", "127.0.0.1"]:
        verifier = PolicyBuilder().store(store).build_server_verifier(x509.IPAddress(ipaddress.ip_address(ip)))
        verifier.verify(leaf, [])

    san = leaf.extensions.get_extension_for_class(x509.SubjectAlternativeName).value
    assert set(san.get_values_for_type(x509.DNSName)) == {"mylaptop.local", "localhost"}
    assert {str(i) for i in san.get_values_for_type(x509.IPAddress)} == {"192.168.1.23", "127.0.0.1"}

    # iOS constraints
    validity = leaf.not_valid_after_utc - leaf.not_valid_before_utc
    assert validity <= dt.timedelta(days=398)
    eku = leaf.extensions.get_extension_for_class(x509.ExtendedKeyUsage).value
    assert ExtendedKeyUsageOID.SERVER_AUTH in eku
    assert leaf.extensions.get_extension_for_class(x509.BasicConstraints).value.ca is False
    bc = ca.extensions.get_extension_for_class(x509.BasicConstraints)
    assert bc.value.ca is True and bc.critical
    ku = ca.extensions.get_extension_for_class(x509.KeyUsage).value
    assert ku.key_cert_sign
    assert leaf.signature_hash_algorithm.name == "sha256"
    assert (key_path.stat().st_mode & 0o777) == 0o600
    assert ((tmp_path / certs.CA_KEY).stat().st_mode & 0o777) == 0o600


def test_ssl_handshake_with_generated_ca(tmp_path):
    cert_path, key_path = certs.ensure_server_cert(tmp_path, ["localhost", "127.0.0.1"])
    server_ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    server_ctx.load_cert_chain(cert_path, key_path)
    client_ctx = ssl.create_default_context(cafile=str(tmp_path / certs.CA_CERT))
    assert client_ctx.verify_mode == ssl.CERT_REQUIRED and client_ctx.check_hostname

    lsock = socket.socket()
    lsock.bind(("127.0.0.1", 0))
    lsock.listen(1)
    port = lsock.getsockname()[1]
    errors = []

    def serve():
        try:
            conn, _ = lsock.accept()
            with server_ctx.wrap_socket(conn, server_side=True) as s:
                s.sendall(s.recv(4))
        except Exception as exc:  # noqa: BLE001
            errors.append(exc)

    th = threading.Thread(target=serve)
    th.start()
    for host in ["localhost"]:
        with socket.create_connection(("127.0.0.1", port)) as raw:
            with client_ctx.wrap_socket(raw, server_hostname=host) as s:
                s.sendall(b"ping")
                assert s.recv(4) == b"ping"
    th.join(5)
    lsock.close()
    assert not errors


def test_ca_reused_and_server_cert_regenerated_on_host_change(tmp_path):
    certs.ensure_server_cert(tmp_path, ["10.0.0.1", "localhost"])
    ca1 = (tmp_path / certs.CA_CERT).read_bytes()
    leaf1 = (tmp_path / certs.SERVER_CERT).read_bytes()

    certs.ensure_server_cert(tmp_path, ["10.0.0.1", "localhost"])
    assert (tmp_path / certs.SERVER_CERT).read_bytes() == leaf1  # unchanged hosts: reused

    certs.ensure_server_cert(tmp_path, ["10.0.0.1"])  # subset: still covered
    assert (tmp_path / certs.SERVER_CERT).read_bytes() == leaf1

    certs.ensure_server_cert(tmp_path, ["10.0.0.2", "localhost"])  # new LAN IP
    leaf2 = load(tmp_path / certs.SERVER_CERT)
    assert (tmp_path / certs.SERVER_CERT).read_bytes() != leaf1
    assert (tmp_path / certs.CA_CERT).read_bytes() == ca1  # phone keeps trusting the same CA
    ips = leaf2.extensions.get_extension_for_class(x509.SubjectAlternativeName).value.get_values_for_type(x509.IPAddress)
    assert ipaddress.ip_address("10.0.0.2") in ips


def test_regenerates_when_ca_was_recreated(tmp_path):
    """If the CA files are deleted (new CA), the old leaf (same issuer name, other key) must not be reused."""
    certs.ensure_server_cert(tmp_path, ["localhost"])
    (tmp_path / certs.CA_CERT).unlink()
    (tmp_path / certs.CA_KEY).unlink()
    cert_path, _ = certs.ensure_server_cert(tmp_path, ["localhost"])
    ca = load(tmp_path / certs.CA_CERT)
    load(cert_path).verify_directly_issued_by(ca)


def test_regenerates_when_close_to_expiry(tmp_path, monkeypatch):
    certs.ensure_server_cert(tmp_path, ["localhost"])
    leaf1 = (tmp_path / certs.SERVER_CERT).read_bytes()
    future = dt.datetime.now(dt.timezone.utc) + dt.timedelta(days=certs.SERVER_VALIDITY_DAYS - 3)
    monkeypatch.setattr(certs, "_now", lambda: future)
    certs.ensure_server_cert(tmp_path, ["localhost"])
    assert (tmp_path / certs.SERVER_CERT).read_bytes() != leaf1


def test_duplicate_hosts_ok(tmp_path):
    cert_path, _ = certs.ensure_server_cert(tmp_path, ["127.0.0.1", "localhost", "127.0.0.1", "localhost"])
    load(cert_path)
