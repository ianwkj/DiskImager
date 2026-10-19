namespace DiskImager.Core;

/// <summary>
/// Public half of the key that signs DiskImager updates. The private half stays with the publisher
/// (%USERPROFILE%\.diskimager\update-signing-key.xml on the build PC, never in the repository);
/// an update is installed only if its signature checks out against this key.
/// </summary>
public static class UpdateKey
{
    public const string PublicKeyXml =
        "<RSAKeyValue><Modulus>qMzUDKCFZvvfgKd/PnUoH8zXKympHzsDIPxL9JlBvB+P/t0/u5Fq9SGxt2RuE11e2+2MvzasAj+Xh9UFtPBQTHovBxy6hINlk6E7A2NbOBNUYrOkGR+K9w1GhYebdrxzoXKl17qqMs3+F0VMFnljUcv4KS/cQChe+IqEGy9iPiEY9CPV9pg5VQm+DQDkAwh0Ce4T7Y8GuChKIi6N9lD2kgnMJ7j/WsQdvmvckBU6ClF/cTfUfzmYm4mB1J9m7tG9wofwYaHLNLguf71cK2mvU4HykBIZ1GP6tkXtTtzPOs9wFM8ywXvcmUqMNOsK9bTiaoQY5jJMtcUmn0I9vL3NHhGPBKM+S14LVCW9CDrHjVZESyT0eA0U0JwSjEbf4kC+ya4bqRSRqAxRAPYprAaTCb1egRfMX+5aBDOqEj4QHlqVdWlvzVR4bBFa04VCZq3uIFvbY0+wqgZakA5lJ/wyWCx1xRWHtMC5Sl3G3Hug05CfMRcPmP+mhtgMqXVZ7eIV</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";
}