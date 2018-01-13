﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;
using NuGet.Packaging.Signing;
using NuGetKeyVaultSignTool.BouncyCastle;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509.Extension;
using Org.BouncyCastle.X509.Store;
using AttributeTable = Org.BouncyCastle.Asn1.Cms.AttributeTable;
using HashAlgorithmName = NuGet.Common.HashAlgorithmName;

namespace NuGetKeyVaultSignTool
{
    class KeyVaultSignatureProvider : ISignatureProvider
    {
        private readonly RSA provider;
        private readonly ITimestampProvider timestampProvider;

        public KeyVaultSignatureProvider(RSA provider, ITimestampProvider timestampProvider)
        {
            this.provider = provider;
            this.timestampProvider = timestampProvider ?? throw new ArgumentNullException(nameof(timestampProvider));
        }

        public async Task<Signature> CreateSignatureAsync(SignPackageRequest request, SignatureContent signatureContent, ILogger logger, CancellationToken token)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (signatureContent == null)
            {
                throw new ArgumentNullException(nameof(signatureContent));
            }

            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            var authorSignature = CreateKeyVaultSignature(request, signatureContent, request.SignatureType);
            var timestamped = await TimestampSignature(request, logger, authorSignature, token);

            return timestamped;
        }

        byte[] CreateKeyVaultSignature(SignPackageRequest request, SignatureContent signatureContent, SignatureType signatureType)
        {
            // Get the chain
            IReadOnlyList<X509Certificate2> certs;


            using (var chain = new X509Chain())
            {
                SetCertBuildChainPolicy(chain, DateTime.Now);
                if (chain.Build(request.Certificate))
                {
                   certs = chain.ChainElements.OfType<X509ChainElement>().Select(ele => ele.Certificate).ToList();
                }
                else
                { 
                    // This may be a test root cert
                    certs = new List<X509Certificate2>
                    {
                        request.Certificate
                    };
                }
            }

            // Get the chain as bc certs
            var additionals = certs.Select(DotNetUtilities.FromX509Certificate).ToList();
            var bcCer = DotNetUtilities.FromX509Certificate(request.Certificate);
            var store = X509StoreFactory.Create("Certificate/Collection", new X509CollectionStoreParameters(additionals));
            
            
            var cti = AttributeUtility.CreateCommitmentTypeIndication(signatureType);
            var cv2 = AttributeUtility.CreateSigningCertificateV2(request.Certificate, request.SignatureHashAlgorithm);

            var attribTable = new AttributeTable(new Asn1EncodableVector
            {
                ToBcAttribute(cti),
                ToBcAttribute(cv2)
            });

            // SignerInfo generator setup
            var signerInfoGeneratorBuilder = new SignerInfoGeneratorBuilder()
                .WithSignedAttributeGenerator(new DefaultSignedAttributeTableGenerator(attribTable));
            

            // Subject Key Identifier (SKI) is smaller and less prone to accidental matching than issuer and serial
            // number.  However, to ensure cross-platform verification, SKI should only be used if the certificate
            // has the SKI extension attribute.

            // Try to look for the value 
            
            var ext = bcCer.GetExtensionValue(new DerObjectIdentifier(Oids.SubjectKeyIdentifier));
            SignerInfoGenerator signerInfoGenerator;
            if (ext != null)
            {
                var ski = new SubjectKeyIdentifierStructure(ext);
                signerInfoGenerator = signerInfoGeneratorBuilder.Build(new RsaSignatureFactory(HashAlgorithmToBouncyCastle(request.SignatureHashAlgorithm), provider), ski.GetKeyIdentifier());
            }
            else
                signerInfoGenerator = signerInfoGeneratorBuilder.Build(new RsaSignatureFactory(HashAlgorithmToBouncyCastle(request.SignatureHashAlgorithm), provider), bcCer);

            
            var generator = new CmsSignedDataGenerator();
            
            generator.AddSignerInfoGenerator(signerInfoGenerator);
            generator.AddCertificates(store);
            
            var msg = new CmsProcessableByteArray(signatureContent.GetBytes());
            var data = generator.Generate(msg, true);

            var encoded = data.GetEncoded();
            return encoded;
        }

        Org.BouncyCastle.Asn1.Cms.Attribute ToBcAttribute(CryptographicAttributeObject obj)
        {
            var encodables = obj.Values.Cast<AsnEncodedData>().Select(d => Asn1Object.FromByteArray(d.RawData)).ToArray();
            var derSet = new DerSet(encodables);

            var attr = new Org.BouncyCastle.Asn1.Cms.Attribute(new DerObjectIdentifier(obj.Oid.Value), derSet);

            return attr;
        }

        static string HashAlgorithmToBouncyCastle(NuGet.Common.HashAlgorithmName algorithmName)
        {
            switch (algorithmName)
            {
                case NuGet.Common.HashAlgorithmName.SHA256:
                    return "SHA256WITHRSA";
                case NuGet.Common.HashAlgorithmName.SHA384:
                    return "SHA384WITHRSA";
                case NuGet.Common.HashAlgorithmName.SHA512:
                    return "SHA512WITHRSA";

                default:
                    throw new ArgumentOutOfRangeException(nameof(algorithmName));
            }
        }


        Task<Signature> TimestampSignature(SignPackageRequest request, ILogger logger, byte[] signature, CancellationToken token)
        {
            var timestampRequest = new TimestampRequest
            {
                SignatureValue = signature,
                SigningSpec = SigningSpecifications.V1,
                TimestampHashAlgorithm = request.TimestampHashAlgorithm
            };

            return timestampProvider.TimestampSignatureAsync(timestampRequest, logger, token);
        }


        internal static void SetCertBuildChainPolicy(
            X509Chain x509Chain,
            DateTime verificationTime)
        {
            var policy = x509Chain.ChainPolicy;
            
            policy.ApplicationPolicy.Add(new Oid(Oids.CodeSigningEku));

            policy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            policy.RevocationMode = X509RevocationMode.Online;

            policy.VerificationTime = verificationTime;
        }

    }
}
