"""Local HTTPS: a self-made root CA (install once on the iPhone) and a server cert for this laptop.

Safari only exposes DeviceOrientation on secure origins, so the page must be
HTTPS. iOS requirements for a trusted server cert: SAN entries, serverAuth
EKU, validity <= 398 days, signed by a CA the user has fully trusted
(Settings > General > About > Certificate Trust Settings).
"""

from __future__ import annotations

import datetime as dt
import ipaddress
from pathlib import Path

from cryptography import x509
from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.x509.oid import ExtendedKeyUsageOID, NameOID

CA_CERT = "rootCA.crt"
CA_KEY = "rootCA.key"
SERVER_CERT = "server.crt"
SERVER_KEY = "server.key"
SERVER_VALIDITY_DAYS = 390


def _now() -> dt.datetime:
    return dt.datetime.now(dt.timezone.utc)


def _write_key(path: Path, key: ec.EllipticCurvePrivateKey) -> None:
    path.write_bytes(key.private_bytes(
        serialization.Encoding.PEM,
        serialization.PrivateFormat.PKCS8,
        serialization.NoEncryption(),
    ))
    path.chmod(0o600)


def _load_key(path: Path) -> ec.EllipticCurvePrivateKey:
    return serialization.load_pem_private_key(path.read_bytes(), password=None)


def ensure_ca(cert_dir: Path) -> tuple[x509.Certificate, ec.EllipticCurvePrivateKey]:
    cert_dir.mkdir(parents=True, exist_ok=True)
    cert_path, key_path = cert_dir / CA_CERT, cert_dir / CA_KEY
    if cert_path.exists() and key_path.exists():
        return x509.load_pem_x509_certificate(cert_path.read_bytes()), _load_key(key_path)

    key = ec.generate_private_key(ec.SECP256R1())
    name = x509.Name([
        x509.NameAttribute(NameOID.COMMON_NAME, "Reachy Phone Teleop local CA"),
        x509.NameAttribute(NameOID.ORGANIZATION_NAME, "ReachyTeleop (local only)"),
    ])
    now = _now()
    cert = (
        x509.CertificateBuilder()
        .subject_name(name)
        .issuer_name(name)
        .public_key(key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(now - dt.timedelta(days=1))
        .not_valid_after(now + dt.timedelta(days=3650))
        .add_extension(x509.BasicConstraints(ca=True, path_length=0), critical=True)
        .add_extension(x509.KeyUsage(
            digital_signature=True, content_commitment=False, key_encipherment=False,
            data_encipherment=False, key_agreement=False, key_cert_sign=True,
            crl_sign=True, encipher_only=False, decipher_only=False), critical=True)
        .add_extension(x509.SubjectKeyIdentifier.from_public_key(key.public_key()), critical=False)
        .sign(key, hashes.SHA256())
    )
    _write_key(key_path, key)
    cert_path.write_bytes(cert.public_bytes(serialization.Encoding.PEM))
    return cert, key


def _san_names(hosts: list[str]) -> list[x509.GeneralName]:
    names: list[x509.GeneralName] = []
    for host in dict.fromkeys(hosts):
        try:
            names.append(x509.IPAddress(ipaddress.ip_address(host)))
        except ValueError:
            names.append(x509.DNSName(host))
    return names


def _cert_covers(cert: x509.Certificate, hosts: list[str], ca: x509.Certificate) -> bool:
    try:
        cert.verify_directly_issued_by(ca)  # also catches a regenerated CA with the same name
    except (ValueError, TypeError, InvalidSignature):
        return False
    if cert.not_valid_after_utc - _now() < dt.timedelta(days=7):
        return False
    try:
        san = cert.extensions.get_extension_for_class(x509.SubjectAlternativeName).value
    except x509.ExtensionNotFound:
        return False
    return set(_san_names(hosts)) <= set(san)


def ensure_server_cert(cert_dir: Path, hosts: list[str]) -> tuple[Path, Path]:
    """Return (cert, key) paths, regenerating if the SANs (e.g. a new LAN IP) changed."""
    ca_cert, ca_key = ensure_ca(cert_dir)
    cert_path, key_path = cert_dir / SERVER_CERT, cert_dir / SERVER_KEY
    if cert_path.exists() and key_path.exists():
        existing = x509.load_pem_x509_certificate(cert_path.read_bytes())
        if _cert_covers(existing, hosts, ca_cert):
            return cert_path, key_path

    key = ec.generate_private_key(ec.SECP256R1())
    now = _now()
    cert = (
        x509.CertificateBuilder()
        .subject_name(x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, hosts[0])]))
        .issuer_name(ca_cert.subject)
        .public_key(key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(now - dt.timedelta(days=1))
        .not_valid_after(now + dt.timedelta(days=SERVER_VALIDITY_DAYS))
        .add_extension(x509.SubjectAlternativeName(_san_names(hosts)), critical=False)
        .add_extension(x509.BasicConstraints(ca=False, path_length=None), critical=True)
        .add_extension(x509.KeyUsage(
            digital_signature=True, content_commitment=False, key_encipherment=False,
            data_encipherment=False, key_agreement=True, key_cert_sign=False,
            crl_sign=False, encipher_only=False, decipher_only=False), critical=True)
        .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH]), critical=False)
        .add_extension(x509.AuthorityKeyIdentifier.from_issuer_public_key(ca_key.public_key()), critical=False)
        .sign(ca_key, hashes.SHA256())
    )
    _write_key(key_path, key)
    cert_path.write_bytes(cert.public_bytes(serialization.Encoding.PEM))
    return cert_path, key_path
