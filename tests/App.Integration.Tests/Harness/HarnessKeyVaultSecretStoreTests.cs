using System.Buffers.Text;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using WorkContinuity.Connectors.Monday;
using WorkContinuity.Core.Tenancy;
using WorkContinuity.Harness.Monday.Secrets;

namespace WorkContinuity.Integration.Tests.Harness;

/// <summary>
/// Gate 3G, offline Key Vault adapter (owner decisions S-1 to S-5 of 2026-10-05): its value encoding, status mapping,
/// attempts and budgets, cancellation, request shapes and leak behaviour, against a fake vault only. Nothing here is
/// evidence of how a real vault behaves; it shows what the adapter does with each documented kind of answer.
/// </summary>
public sealed class HarnessKeyVaultSecretStoreTests
{
    private const string Token = "synthetic-vault-access-token";

    private static readonly MondaySecretName Verifier = ConnectorSecretNames.Verifier(TenantId.New(), Guid.CreateVersion7());
    private static readonly MondaySecretName Tokens = ConnectorSecretNames.Tokens(TenantId.New(), Guid.CreateVersion7(), Guid.CreateVersion7());

    private CancellationToken Cancellation => TestContext.Current.CancellationToken;

    // --- S-3: encoding and size -----------------------------------------------------------------------------------

    [Fact]
    public async Task Every_byte_sequence_up_to_the_bound_round_trips_exactly_including_invalid_utf8()
    {
        var random = new Random(20261005);
        List<byte[]> values =
        [
            [],
            [0x00],
            [0xC0, 0x80],
            [0xFF, 0xFE, 0xFD],
            [0x80, 0x80, 0x80, 0x80],
            [0xED, 0xA0, 0x80],
            Enumerable.Range(0, 256).Select(value => (byte)value).ToArray(),
        ];
        foreach (var length in new[] { 1, 2, 3, 4, 5, 1023, 1024, 17_999, KeyVaultSecretCodec.MaxValueBytes })
        {
            var bytes = new byte[length];
            random.NextBytes(bytes);
            values.Add(bytes);
        }

        var (store, vault, _, _, _) = Compose();
        foreach (var value in values)
        {
            Assert.Equal(MondaySecretStoreStatus.Succeeded, await store.SetAsync(Verifier, value, Cancellation));
            var (text, contentType) = vault.Current(Verifier.Value);
            Assert.Equal(KeyVaultSecretCodec.ContentType, contentType);
            Assert.Equal(Base64Url.EncodeToString(value), text);
            Assert.DoesNotContain('=', text);

            var read = await store.GetAsync(Verifier, Cancellation);
            Assert.Equal(MondaySecretStoreStatus.Succeeded, read.Status);
            Assert.True(ValueOf(read).AsSpan().SequenceEqual(value), $"length {value.Length}");
        }

        // The largest supported value is exactly the bound, and its encoded text fits the documented vault limit.
        Assert.Equal(KeyVaultSecretCodec.MaxEncodedLength, Base64Url.EncodeToString(new byte[KeyVaultSecretCodec.MaxValueBytes]).Length);
        Assert.True(KeyVaultSecretCodec.MaxEncodedLength <= 25_000);
    }

    [Fact]
    public async Task Plain_text_without_a_content_type_is_read_only_under_the_two_owner_placed_names()
    {
        var (store, vault, _, _, _) = Compose();
        vault.Place(MondaySecretName.ClientSecret, "owner-placed-client-credential");
        vault.Place(MondaySecretName.SigningSecret, "owner-placed-signing-key");
        vault.Place(Verifier, "unmarked-under-a-connector-name");
        vault.Place(Tokens, "unmarked-under-a-connector-name");

        Assert.Equal("owner-placed-client-credential"u8.ToArray(), ValueOf(await store.GetAsync(MondaySecretName.ClientSecret, Cancellation)));
        Assert.Equal("owner-placed-signing-key"u8.ToArray(), ValueOf(await store.GetAsync(MondaySecretName.SigningSecret, Cancellation)));
        Assert.Equal(MondaySecretStoreStatus.Unavailable, (await store.GetAsync(Verifier, Cancellation)).Status);
        Assert.Equal(MondaySecretStoreStatus.Unavailable, (await store.GetAsync(Tokens, Cancellation)).Status);

        // A value an explicit JSON null content type carries is read as having none.
        vault.Then((_, _, _) => Task.FromResult(FakeVault.Json(HttpStatusCode.OK, """{"value":"owner-placed","contentType":null,"id":"x"}""")));
        Assert.Equal("owner-placed"u8.ToArray(), ValueOf(await store.GetAsync(MondaySecretName.ClientSecret, Cancellation)));
        vault.Then((_, _, _) => Task.FromResult(FakeVault.Json(HttpStatusCode.OK, """{"value":"owner-placed","contentType":null,"id":"x"}""")));
        Assert.Equal(MondaySecretStoreStatus.Unavailable, (await store.GetAsync(Verifier, Cancellation)).Status);

        // Plain text is exact UTF-8 and never a lone surrogate: refused by the JSON reader through the adapter, and by the
        // codec itself when it is handed such text directly.
        vault.Then((_, _, _) => Task.FromResult(FakeVault.Json(HttpStatusCode.OK, """{"value":"bad\ud800text","id":"x"}""")));
        Assert.Equal(MondaySecretStoreStatus.Unavailable, (await store.GetAsync(MondaySecretName.ClientSecret, Cancellation)).Status);
        Assert.Equal(MondaySecretStoreStatus.Unavailable, KeyVaultSecretCodec.Decode(MondaySecretName.ClientSecret, "bad\ud800text", false, null).Status);
        Assert.Equal("café"u8.ToArray(), ValueOf(KeyVaultSecretCodec.Decode(MondaySecretName.ClientSecret, "café", false, null)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("text/plain")]
    [InlineData("application/json")]
    [InlineData("application/vnd.wce.opaque-bytes+base64url;v=2")]
    [InlineData("APPLICATION/VND.WCE.OPAQUE-BYTES+BASE64URL;V=1")]
    [InlineData("application/vnd.wce.opaque-bytes+base64url;v=1 ")]
    [InlineData(" application/vnd.wce.opaque-bytes+base64url;v=1")]
    public async Task An_unknown_content_type_is_unavailable_under_every_name(string contentType)
    {
        var (store, vault, _, _, _) = Compose();
        foreach (var name in new[] { MondaySecretName.ClientSecret, MondaySecretName.SigningSecret, Verifier, Tokens })
        {
            vault.Place(name, "c2VjcmV0", contentType);
            Assert.Equal(MondaySecretStoreStatus.Unavailable, (await store.GetAsync(name, Cancellation)).Status);
        }
    }

    [Theory]
    [InlineData("YWJj=")]
    [InlineData("YWI=")]
    [InlineData("YW+j")]
    [InlineData("YW/j")]
    [InlineData("YW j")]
    [InlineData("YWJ")]
    [InlineData("YWI")]
    [InlineData("YWJjZ")]
    [InlineData("YWK")]
    [InlineData("owner-placed-client-credential")]
    public async Task A_malformed_marked_value_is_unavailable_and_never_falls_back_to_plain_text(string text)
    {
        var (store, vault, _, _, _) = Compose();

        // Each of these is visible ASCII that would be a well-formed client secret if read as plain text. "YWI" is the
        // canonical encoding of "ab", the positive control; "YWJ" and "YWK" carry unused trailing bits the adapter never
        // writes.
        var canonical = text == "YWI";
        vault.Place(MondaySecretName.ClientSecret, text, KeyVaultSecretCodec.ContentType);
        var read = await store.GetAsync(MondaySecretName.ClientSecret, Cancellation);
        if (canonical)
        {
            Assert.Equal("ab"u8.ToArray(), ValueOf(read));
        }
        else
        {
            Assert.Equal(MondaySecretStoreStatus.Unavailable, read.Status);
        }

        // Too long to be anything the adapter writes, or decoding to more than the bound.
        vault.Place(Verifier, new string('A', KeyVaultSecretCodec.MaxEncodedLength + 4), KeyVaultSecretCodec.ContentType);
        Assert.Equal(MondaySecretStoreStatus.Unavailable, (await store.GetAsync(Verifier, Cancellation)).Status);
        vault.Place(Verifier, Base64Url.EncodeToString(new byte[KeyVaultSecretCodec.MaxValueBytes + 1]), KeyVaultSecretCodec.ContentType);
        Assert.Equal(MondaySecretStoreStatus.Unavailable, (await store.GetAsync(Verifier, Cancellation)).Status);
    }

    [Fact]
    public async Task A_value_above_the_bound_is_refused_before_any_request()
    {
        var (store, vault, _, _, tokens) = Compose();
        Assert.Equal(MondaySecretStoreStatus.Unavailable, await store.SetAsync(Verifier, new byte[KeyVaultSecretCodec.MaxValueBytes + 1], Cancellation));
        Assert.Equal(MondaySecretStoreStatus.Unavailable, await store.SetAsync(Tokens, new byte[100_000], Cancellation));
        Assert.Empty(vault.Exchanges);
        Assert.Equal(0, tokens.Calls);
        Assert.Null(KeyVaultSecretCodec.Encode(new byte[KeyVaultSecretCodec.MaxValueBytes + 1]));

        // Positive control: the bound itself is sent.
        Assert.Equal(MondaySecretStoreStatus.Succeeded, await store.SetAsync(Verifier, new byte[KeyVaultSecretCodec.MaxValueBytes], Cancellation));
        Assert.Single(vault.Exchanges);
    }

    // --- S-4 and S-5: status mapping ------------------------------------------------------------------------------

    [Fact]
    public async Task Get_set_and_delete_succeed_and_a_qualifying_not_found_is_not_found()
    {
        var (store, vault, _, _, _) = Compose();
        Assert.Equal(MondaySecretStoreStatus.NotFound, (await store.GetAsync(Verifier, Cancellation)).Status);
        Assert.Equal(MondaySecretStoreStatus.NotFound, await store.DeleteAsync(Verifier, Cancellation));
        Assert.Equal(MondaySecretStoreStatus.Succeeded, await store.SetAsync(Verifier, "v"u8.ToArray(), Cancellation));
        Assert.Equal("v"u8.ToArray(), ValueOf(await store.GetAsync(Verifier, Cancellation)));

        // A delete's success means the name left the active namespace: an ordinary get no longer finds it, while the
        // vault still holds it as deleted, and the name stays reserved.
        Assert.Equal(MondaySecretStoreStatus.Succeeded, await store.DeleteAsync(Verifier, Cancellation));
        Assert.Equal(MondaySecretStoreStatus.NotFound, (await store.GetAsync(Verifier, Cancellation)).Status);
        Assert.Equal(MondaySecretStoreStatus.NotFound, await store.DeleteAsync(Verifier, Cancellation));
        Assert.True(vault.IsDeleted(Verifier.Value));
        Assert.Equal(MondaySecretStoreStatus.Unavailable, await store.SetAsync(Verifier, "v"u8.ToArray(), Cancellation));
        Assert.All(vault.Exchanges, exchange => Assert.NotEqual(HttpMethod.Post, exchange.Method));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("<html>Not Found</html>")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"error":"SecretNotFound"}""")]
    [InlineData("""{"error":{}}""")]
    [InlineData("""{"error":{"code":""}}""")]
    [InlineData("""{"error":{"code":42}}""")]
    [InlineData("""{"Error":{"code":"SecretNotFound"}}""")]
    public async Task A_not_found_without_a_well_formed_key_vault_error_is_unavailable(string body)
    {
        var (store, vault, _, _, _) = Compose();
        vault.Then(FakeVault.Status(HttpStatusCode.NotFound, body));
        Assert.Equal(MondaySecretStoreStatus.Unavailable, (await store.GetAsync(Verifier, Cancellation)).Status);
        vault.Then(FakeVault.Status(HttpStatusCode.NotFound, body));
        Assert.Equal(MondaySecretStoreStatus.Unavailable, await store.DeleteAsync(Verifier, Cancellation));
        Assert.Equal(2, vault.Exchanges.Count);

        // Positive control: the same answer with a Key Vault error body is not found.
        vault.Then(FakeVault.Status(HttpStatusCode.NotFound, FakeVault.NotFoundBody));
        Assert.Equal(MondaySecretStoreStatus.NotFound, (await store.GetAsync(Verifier, Cancellation)).Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(HttpStatusCode.NotImplemented)]
    [InlineData(HttpStatusCode.HttpVersionNotSupported)]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task Refusals_conflicts_and_other_answers_are_unavailable_after_one_attempt(HttpStatusCode status)
    {
        var (store, vault, _, delay, _) = Compose();
        vault.Then(FakeVault.Status(status, """{"error":{"code":"Any","message":"refused"}}"""));
        Assert.Equal(MondaySecretStoreStatus.Unavailable, (await store.GetAsync(Verifier, Cancellation)).Status);
        vault.Then(FakeVault.Status(status, """{"error":{"code":"Any","message":"refused"}}"""));
        Assert.Equal(MondaySecretStoreStatus.Unavailable, await store.SetAsync(Verifier, "v"u8.ToArray(), Cancellation));
        vault.Then(FakeVault.Status(status, """{"error":{"code":"Any","message":"refused"}}"""));
        Assert.Equal(MondaySecretStoreStatus.Unavailable, await store.DeleteAsync(Verifier, Cancellation));
        Assert.Equal(3, vault.Exchanges.Count);
        Assert.Empty(delay.Waits);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"id":"x"}""")]
    [InlineData("""{"value":42}""")]
    [InlineData("""{"value":null}""")]
    [InlineData("""{"value":"YWJj","contentType":7}""")]
    public async Task A_malformed_success_is_unavailable(string body)
    {
        var (store, vault, _, _, _) = Compose();
        vault.Then(FakeVault.Status(HttpStatusCode.OK, body));
        Assert.Equal(MondaySecretStoreStatus.Unavailable, (await store.GetAsync(MondaySecretName.ClientSecret, Cancellation)).Status);
        Assert.Single(vault.Exchanges);

        if (body is "" or "not json" or "[]")
        {
            vault.Then(FakeVault.Status(HttpStatusCode.OK, body));
            Assert.Equal(MondaySecretStoreStatus.Unavailable, await store.SetAsync(Verifier, "v"u8.ToArray(), Cancellation));
            vault.Then(FakeVault.Status(HttpStatusCode.OK, body));
            Assert.Equal(MondaySecretStoreStatus.Unavailable, await store.DeleteAsync(Verifier, Cancellation));
        }

        // A body above the response bound is malformed too.
        vault.Then(FakeVault.Status(HttpStatusCode.OK, "{\"value\":\"" + new string('A', 70_000) + "\"}"));
        Assert.Equal(MondaySecretStoreStatus.Unavailable, (await store.GetAsync(MondaySecretName.ClientSecret, Cancellation)).Status);
    }

    [Fact]
    public async Task A_set_answered_not_found_is_unavailable_and_a_token_source_failure_sends_nothing()
    {
        var (store, vault, _, _, _) = Compose();
        vault.Then(FakeVault.Status(HttpStatusCode.NotFound, FakeVault.NotFoundBody));
        Assert.Equal(MondaySecretStoreStatus.Unavailable, await store.SetAsync(Verifier, "v"u8.ToArray(), Cancellation));

        foreach (var source in new[]
                 {
                     new FixedTokenSource(null),
                     new FixedTokenSource(string.Empty),
                     new FixedTokenSource("has space"),
                     new FixedTokenSource(new string('t', 16 * 1024 + 1)),
                     new FixedTokenSource(Token) { Failure = new InvalidOperationException("no credential") },
                 })
        {
            var time = new SteppedTimeProvider();
            var failing = new FakeVault(time);
            var adapter = new KeyVaultSecretStore(FakeVault.Origin, failing, source, new RecordingDelay(time), time);
            Assert.Equal(MondaySecretStoreStatus.Unavailable, (await adapter.GetAsync(Verifier, Cancellation)).Status);
            Assert.Equal(MondaySecretStoreStatus.Unavailable, await adapter.SetAsync(Verifier, "v"u8.ToArray(), Cancellation));
            Assert.Equal(MondaySecretStoreStatus.Unavailable, await adapter.DeleteAsync(Verifier, Cancellation));
            Assert.Empty(failing.Exchanges);
        }
    }

    // --- S-5: attempts, waits and budgets ---------------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task Only_the_retryable_classes_are_retried_at_most_three_times_with_the_default_waits(HttpStatusCode status)
    {
        foreach (var operation in Operations)
        {
            var (store, vault, _, delay, _) = Compose();
            for (var answer = 0; answer < 4; answer++)
            {
                vault.Then(FakeVault.Status(status));
            }

            Assert.Equal(MondaySecretStoreStatus.Unavailable, await operation(store));
            Assert.Equal(3, vault.Exchanges.Count);
            Assert.Equal([TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(800)], delay.Waits);

            // The third attempt settles it when it succeeds.
            var (settled, settledVault, _, _, _) = Compose();
            settledVault.Place(Verifier, "dg", KeyVaultSecretCodec.ContentType);
            settledVault.Then(FakeVault.Status(status));
            settledVault.Then(FakeVault.Status(status));
            Assert.NotEqual(MondaySecretStoreStatus.Unavailable, await operation(settled));
            Assert.Equal(3, settledVault.Exchanges.Count);
        }

        // A connection failure is retried the same way.
        var (failing, failingVault, _, failingDelay, _) = Compose();
        for (var answer = 0; answer < 3; answer++)
        {
            failingVault.Then(FakeVault.Fail());
        }

        Assert.Equal(MondaySecretStoreStatus.Unavailable, (await failing.GetAsync(Verifier, Cancellation)).Status);
        Assert.Equal(3, failingVault.Exchanges.Count);
        Assert.Equal(2, failingDelay.Waits.Count);
    }

    [Fact]
    public async Task Retry_after_is_honored_up_to_two_seconds_within_the_budget_and_a_longer_one_ends_the_operation()
    {
        foreach (var (retryAfter, expectedWait) in new (TimeSpan, TimeSpan?)[]
                 {
                     (TimeSpan.Zero, TimeSpan.Zero),
                     (TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)),
                     (TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)),
                     (TimeSpan.FromSeconds(3), null),
                     (TimeSpan.FromSeconds(120), null),
                 })
        {
            var (store, vault, _, delay, _) = Compose();
            vault.Place(Verifier, "dg", KeyVaultSecretCodec.ContentType);
            vault.Then(FakeVault.Status(HttpStatusCode.TooManyRequests, retryAfter: retryAfter));
            var read = await store.GetAsync(Verifier, Cancellation);
            if (expectedWait is { } wait)
            {
                Assert.Equal(MondaySecretStoreStatus.Succeeded, read.Status);
                Assert.Equal([wait], delay.Waits);
                Assert.Equal(2, vault.Exchanges.Count);
            }
            else
            {
                Assert.Equal(MondaySecretStoreStatus.Unavailable, read.Status);
                Assert.Empty(delay.Waits);
                Assert.Single(vault.Exchanges);
            }
        }

        // An HTTP date is read against the time source.
        var (dated, datedVault, time, datedDelay, _) = Compose();
        datedVault.Place(Verifier, "dg", KeyVaultSecretCodec.ContentType);
        datedVault.Then(FakeVault.Status(HttpStatusCode.ServiceUnavailable, retryAt: time.GetUtcNow() + TimeSpan.FromSeconds(1)));
        Assert.Equal(MondaySecretStoreStatus.Succeeded, (await dated.GetAsync(Verifier, Cancellation)).Status);
        Assert.Equal([TimeSpan.FromSeconds(1)], datedDelay.Waits);

        // Within the cap but beyond what is left of the budget: the operation ends instead of waiting.
        var (late, lateVault, lateTime, lateDelay, _) = Compose();
        lateVault.Then(FakeVault.Hang());
        lateVault.Then((vault, exchange, cancellationToken) =>
        {
            lateTime.Advance(TimeSpan.FromSeconds(3));
            return FakeVault.Status(HttpStatusCode.ServiceUnavailable, retryAfter: TimeSpan.FromSeconds(2))(vault, exchange, cancellationToken);
        });
        Assert.Equal(MondaySecretStoreStatus.Unavailable, (await late.GetAsync(Verifier, Cancellation)).Status);
        Assert.Equal([TimeSpan.FromMilliseconds(200)], lateDelay.Waits);
        Assert.Equal(2, lateVault.Exchanges.Count);
    }

    [Fact]
    public async Task Each_attempt_is_bounded_to_five_seconds_and_each_operation_to_its_budget()
    {
        // Get: 5 s, a 200 ms wait, then what is left of 10 s; the 800 ms wait no longer fits.
        var (get, getVault, getTime, getDelay, _) = Compose();
        for (var answer = 0; answer < 3; answer++)
        {
            getVault.Then(FakeVault.Hang());
        }

        Assert.Equal(MondaySecretStoreStatus.Unavailable, (await get.GetAsync(Verifier, Cancellation)).Status);
        Assert.Equal(2, getVault.Exchanges.Count);
        Assert.Equal([TimeSpan.FromMilliseconds(200)], getDelay.Waits);
        // The approved values themselves, not the adapter's constants: a changed constant must fail here.
        Assert.Equal(TimeSpan.FromSeconds(10), getTime.Elapsed);

        // Set and delete: three attempts of 5 s, 5 s and what is left of 15 s.
        foreach (var write in new Func<KeyVaultSecretStore, Task<MondaySecretStoreStatus>>[]
                 {
                     store => store.SetAsync(Verifier, "v"u8.ToArray(), Cancellation).AsTask(),
                     store => store.DeleteAsync(Verifier, Cancellation).AsTask(),
                 })
        {
            var (store, vault, time, delay, _) = Compose();
            for (var answer = 0; answer < 3; answer++)
            {
                vault.Then(FakeVault.Hang());
            }

            Assert.Equal(MondaySecretStoreStatus.Unavailable, await write(store));
            Assert.Equal(3, vault.Exchanges.Count);
            Assert.Equal([TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(800)], delay.Waits);
            Assert.Equal(TimeSpan.FromSeconds(15), time.Elapsed);
        }

        // One attempt that times out is retried; the next one settles the operation.
        var (single, singleVault, singleTime, _, _) = Compose();
        singleVault.Place(Verifier, "dg", KeyVaultSecretCodec.ContentType);
        singleVault.Then(FakeVault.Hang());
        Assert.Equal(MondaySecretStoreStatus.Succeeded, (await single.GetAsync(Verifier, Cancellation)).Status);
        Assert.Equal(TimeSpan.FromSeconds(5.2), singleTime.Elapsed);
    }

    [Fact]
    public async Task A_retried_set_resends_identical_bytes_to_the_same_name_and_a_conflict_is_never_retried()
    {
        var (store, vault, _, _, _) = Compose();
        vault.Then(FakeVault.Status(HttpStatusCode.ServiceUnavailable));
        vault.Then(FakeVault.Fail());
        Assert.Equal(MondaySecretStoreStatus.Succeeded, await store.SetAsync(Verifier, "identical"u8.ToArray(), Cancellation));
        var puts = vault.Exchanges.Where(exchange => exchange.Method == HttpMethod.Put).ToList();
        Assert.Equal(3, puts.Count);
        Assert.All(puts, put => Assert.Equal(puts[0].Uri, put.Uri));
        Assert.All(puts, put => Assert.True(put.Body!.AsSpan().SequenceEqual(puts[0].Body), "every attempt sends the same bytes"));
        Assert.Equal(1, vault.Versions(Verifier.Value));

        // A conflict, here a set under a deleted name, is answered once and never retried, by any operation.
        Assert.Equal(MondaySecretStoreStatus.Succeeded, await store.DeleteAsync(Verifier, Cancellation));
        var before = vault.Exchanges.Count;
        Assert.Equal(MondaySecretStoreStatus.Unavailable, await store.SetAsync(Verifier, "identical"u8.ToArray(), Cancellation));
        Assert.Equal(before + 1, vault.Exchanges.Count);
        Assert.False(vault.IsActive(Verifier.Value));
    }

    [Fact]
    public async Task An_ambiguous_set_that_a_later_attempt_settles_succeeds_and_leaves_identical_versions()
    {
        var (store, vault, _, _, _) = Compose();
        vault.Then(FakeVault.ApplyThenLose());
        Assert.Equal(MondaySecretStoreStatus.Succeeded, await store.SetAsync(Verifier, "settled"u8.ToArray(), Cancellation));
        Assert.Equal(2, vault.Versions(Verifier.Value));
        Assert.Equal("settled"u8.ToArray(), ValueOf(await store.GetAsync(Verifier, Cancellation)));
    }

    [Fact]
    public async Task An_ambiguous_delete_followed_by_a_qualifying_not_found_is_not_found()
    {
        var (store, vault, _, _, _) = Compose();
        vault.Place(Verifier, "dg", KeyVaultSecretCodec.ContentType);
        vault.Then(FakeVault.ApplyThenLose());
        Assert.Equal(MondaySecretStoreStatus.NotFound, await store.DeleteAsync(Verifier, Cancellation));
        Assert.Equal(2, vault.Exchanges.Count);
        Assert.True(vault.IsDeleted(Verifier.Value));
    }

    [Fact]
    public async Task Unsettled_ambiguity_is_unavailable_and_is_never_proof_that_the_write_did_not_happen()
    {
        // Every answer is lost, though the vault applied each request: the set is unavailable, and the value is there.
        var (store, vault, _, _, _) = Compose();
        for (var answer = 0; answer < 3; answer++)
        {
            vault.Then(FakeVault.ApplyThenLose());
        }

        Assert.Equal(MondaySecretStoreStatus.Unavailable, await store.SetAsync(Verifier, "applied"u8.ToArray(), Cancellation));
        Assert.True(vault.IsActive(Verifier.Value));

        // The same with attempts that time out after the vault applied them.
        var (timed, timedVault, _, _, _) = Compose();
        for (var answer = 0; answer < 3; answer++)
        {
            timedVault.Then(FakeVault.ApplyThenHang());
        }

        Assert.Equal(MondaySecretStoreStatus.Unavailable, await timed.SetAsync(Tokens, "applied"u8.ToArray(), Cancellation));
        Assert.True(timedVault.IsActive(Tokens.Value));

        // A delete whose every answer is lost is unavailable, though the name is gone.
        var (deleting, deletingVault, _, _, _) = Compose();
        deletingVault.Place(Verifier, "dg", KeyVaultSecretCodec.ContentType);
        deletingVault.Then(FakeVault.ApplyThenLose());
        deletingVault.Then(FakeVault.Fail());
        deletingVault.Then(FakeVault.Fail());
        Assert.Equal(MondaySecretStoreStatus.Unavailable, await deleting.DeleteAsync(Verifier, Cancellation));
        Assert.True(deletingVault.IsDeleted(Verifier.Value));
    }

    [Fact]
    public async Task Caller_cancellation_propagates_before_during_and_between_attempts()
    {
        foreach (var operation in CancellableOperations)
        {
            // Before: nothing is asked of the token source or the vault.
            var (before, beforeVault, _, _, beforeTokens) = Compose();
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation(before, cancelled.Token));
            }

            Assert.Empty(beforeVault.Exchanges);
            Assert.Equal(0, beforeTokens.Calls);

            // During a request: no further attempt.
            var (during, duringVault, _, _, _) = Compose();
            using (var caller = new CancellationTokenSource())
            {
                duringVault.Then((_, _, cancellationToken) =>
                {
                    caller.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult(FakeVault.Json(HttpStatusCode.OK, "{}"));
                });
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation(during, caller.Token));
            }

            Assert.Single(duringVault.Exchanges);

            // Between attempts: the wait sees the cancellation.
            var (between, betweenVault, _, betweenDelay, _) = Compose();
            using (var caller = new CancellationTokenSource())
            {
                betweenVault.Then(FakeVault.Status(HttpStatusCode.ServiceUnavailable));
                betweenDelay.OnWait = caller.Cancel;
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation(between, caller.Token));
            }

            Assert.Single(betweenVault.Exchanges);
        }
    }

    [Fact]
    public async Task No_value_or_absence_is_kept_between_calls()
    {
        var (store, vault, _, _, _) = Compose();
        Assert.Equal(MondaySecretStoreStatus.NotFound, (await store.GetAsync(MondaySecretName.SigningSecret, Cancellation)).Status);
        vault.Place(MondaySecretName.SigningSecret, "first-signing-key");
        Assert.Equal("first-signing-key"u8.ToArray(), ValueOf(await store.GetAsync(MondaySecretName.SigningSecret, Cancellation)));
        vault.Place(MondaySecretName.SigningSecret, "rotated-signing-key");
        Assert.Equal("rotated-signing-key"u8.ToArray(), ValueOf(await store.GetAsync(MondaySecretName.SigningSecret, Cancellation)));
        Assert.Equal(3, vault.Exchanges.Count);

        // The adapter holds only its configuration: every field is read-only, and none can hold a value or an absence.
        var allowed = new[] { typeof(Uri), typeof(HttpMessageHandler), typeof(IKeyVaultAccessTokenSource), typeof(IMondayDelay), typeof(TimeProvider) };
        var instanceFields = typeof(KeyVaultSecretStore).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.Equal(5, instanceFields.Length);
        Assert.All(instanceFields, field => Assert.True(field.IsInitOnly && allowed.Contains(field.FieldType), field.Name));
        Assert.All(
            typeof(KeyVaultSecretStore).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public),
            field => Assert.True(field.IsInitOnly || field.IsLiteral, field.Name));
        Assert.All(
            typeof(KeyVaultSecretCodec).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public),
            field => Assert.True(field.IsInitOnly || field.IsLiteral, field.Name));
    }

    [Fact]
    public async Task Each_tenants_names_reach_only_their_own_entries_in_a_shared_vault()
    {
        // Two tenants with the same connection and version ids: only the tenant in the connector-built name differs.
        var (store, vault, _, _, _) = Compose();
        var connection = Guid.CreateVersion7();
        var version = Guid.CreateVersion7();
        var first = ConnectorSecretNames.Tokens(TenantId.New(), connection, version);
        var second = ConnectorSecretNames.Tokens(TenantId.New(), connection, version);
        Assert.NotEqual(first.Value, second.Value);

        Assert.Equal(MondaySecretStoreStatus.Succeeded, await store.SetAsync(first, "first-tenant"u8.ToArray(), Cancellation));
        Assert.Equal(MondaySecretStoreStatus.Succeeded, await store.SetAsync(second, "second-tenant"u8.ToArray(), Cancellation));
        Assert.Equal("first-tenant"u8.ToArray(), ValueOf(await store.GetAsync(first, Cancellation)));
        Assert.Equal("second-tenant"u8.ToArray(), ValueOf(await store.GetAsync(second, Cancellation)));

        // A write and a delete under one tenant's name leave the other tenant's entry exactly as it was.
        var untouched = vault.Current(second.Value);
        Assert.Equal(MondaySecretStoreStatus.Succeeded, await store.SetAsync(first, "first-tenant-again"u8.ToArray(), Cancellation));
        Assert.Equal(MondaySecretStoreStatus.Succeeded, await store.DeleteAsync(first, Cancellation));
        Assert.Equal(untouched, vault.Current(second.Value));
        Assert.Equal(1, vault.Versions(second.Value));
        Assert.Equal(MondaySecretStoreStatus.NotFound, (await store.GetAsync(first, Cancellation)).Status);
        Assert.Equal("second-tenant"u8.ToArray(), ValueOf(await store.GetAsync(second, Cancellation)));

        // Every request addressed exactly one of the two names, and each name only as often as it was used.
        var paths = vault.Exchanges.Select(exchange => exchange.Uri.AbsolutePath.TrimEnd('/')).ToList();
        Assert.All(paths, path => Assert.Contains(path, new[] { "/secrets/" + first.Value, "/secrets/" + second.Value }));
        Assert.Equal(5, paths.Count(path => path == "/secrets/" + first.Value));
        Assert.Equal(3, paths.Count(path => path == "/secrets/" + second.Value));
    }

    // --- Request shapes and the vault address -----------------------------------------------------------------------

    [Fact]
    public async Task Only_the_configured_vault_and_the_three_request_shapes_are_ever_sent()
    {
        var (store, vault, _, _, _) = Compose();
        vault.Place(MondaySecretName.ClientSecret, "owner-placed");
        foreach (var name in new[] { MondaySecretName.ClientSecret, MondaySecretName.SigningSecret, Verifier, Tokens })
        {
            await store.GetAsync(name, Cancellation);
            await store.SetAsync(name, "v"u8.ToArray(), Cancellation);
            await store.GetAsync(name, Cancellation);
            await store.DeleteAsync(name, Cancellation);
            await store.DeleteAsync(name, Cancellation);
        }

        Assert.Equal(20, vault.Exchanges.Count);
        Assert.All(vault.Exchanges, exchange =>
        {
            Assert.Equal(Uri.UriSchemeHttps, exchange.Uri.Scheme);
            Assert.Equal(FakeVault.Host, exchange.Uri.Host);
            Assert.Equal(443, exchange.Uri.Port);
            Assert.Equal("?api-version=2025-07-01", exchange.Uri.Query);
            Assert.Equal("Bearer " + Token, exchange.Authorization);
            var segments = exchange.Uri.AbsolutePath.Split('/');
            Assert.Equal("secrets", segments[1]);
            if (exchange.Method == HttpMethod.Get)
            {
                // The latest version: the version segment is empty, never named.
                Assert.Equal(4, segments.Length);
                Assert.Equal(string.Empty, segments[3]);
                Assert.Null(exchange.Body);
            }
            else
            {
                Assert.Contains(exchange.Method, new[] { HttpMethod.Put, HttpMethod.Delete });
                Assert.Equal(3, segments.Length);
            }

            if (exchange.Method == HttpMethod.Put)
            {
                Assert.Equal("application/json", exchange.MediaType);
                using var body = JsonDocument.Parse(exchange.Body!);
                Assert.Equal(["contentType", "value"], body.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
                Assert.Equal(KeyVaultSecretCodec.ContentType, body.RootElement.GetProperty("contentType").GetString());
            }

            if (exchange.Method == HttpMethod.Delete)
            {
                Assert.Null(exchange.Body);
            }
        });
    }

    [Theory]
    [InlineData("http://wce-test-vault.vault.azure.net/")]
    [InlineData("https://wce-test-vault.vault.azure.net:8443/")]
    [InlineData("https://wce-test-vault.vault.azure.net/secrets/")]
    [InlineData("https://wce-test-vault.vault.azure.net/?api-version=2025-07-01")]
    [InlineData("https://wce-test-vault.vault.azure.net/#fragment")]
    [InlineData("https://user@wce-test-vault.vault.azure.net/")]
    [InlineData("https://wce-test-vault.vault.azure.net.example.test/")]
    [InlineData("https://vault.azure.net/")]
    [InlineData("https://ab.vault.azure.net/")]
    [InlineData("https://a.b.vault.azure.net/")]
    [InlineData("https://wce-test-vault.example.test/")]
    [InlineData("https://10.0.0.1/")]
    [InlineData("https://[::1]/")]
    [InlineData("https://wce_test.vault.azure.net/")]
    public void A_vault_address_that_is_not_exactly_a_vault_origin_is_refused(string address)
    {
        var time = new SteppedTimeProvider();
        var exception = Assert.Throws<ArgumentException>(() =>
            new KeyVaultSecretStore(new Uri(address), new FakeVault(time), new FixedTokenSource(Token), new RecordingDelay(time), time));
        Assert.False(KeyVaultSecretStore.IsVaultOrigin(new Uri(address)));

        // The refusal is one fixed message: it never quotes the address it refused.
        Assert.DoesNotContain(address, exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            "The vault address must be https://{vault-name}.vault.azure.net/ and nothing more. (Parameter 'vault')",
            exception.Message);

        // Positive control: the configured origin is accepted.
        Assert.True(KeyVaultSecretStore.IsVaultOrigin(FakeVault.Origin));
    }

    [Theory]
    [InlineData("https://WCE-TEST-VAULT.vault.azure.net/")]
    [InlineData("https://wce-test-vault.VAULT.AZURE.NET/")]
    [InlineData("https://wce-test-vault.vault.azure.net")]
    [InlineData("https://wce-test-vault.vault.azure.net:443/")]
    public async Task A_different_spelling_of_the_same_origin_is_the_same_origin_and_every_request_stays_on_it(string address)
    {
        // Host names are case-insensitive, the root path may be implied and 443 is the effective HTTPS port: these
        // spellings name the configured origin, not another one. Each request goes to the one normalized host on 443.
        var time = new SteppedTimeProvider();
        var vault = new FakeVault(time);
        var store = new KeyVaultSecretStore(new Uri(address), vault, new FixedTokenSource(Token), new RecordingDelay(time), time);
        Assert.Equal(MondaySecretStoreStatus.Succeeded, await store.SetAsync(Verifier, "v"u8.ToArray(), Cancellation));
        Assert.Equal("v"u8.ToArray(), ValueOf(await store.GetAsync(Verifier, Cancellation)));
        Assert.Equal(MondaySecretStoreStatus.Succeeded, await store.DeleteAsync(Verifier, Cancellation));
        Assert.Equal(3, vault.Exchanges.Count);
        Assert.All(vault.Exchanges, exchange =>
        {
            Assert.Equal(Uri.UriSchemeHttps, exchange.Uri.Scheme);
            Assert.Equal(FakeVault.Host, exchange.Uri.Host);
            Assert.Equal(443, exchange.Uri.Port);
            Assert.Equal("?api-version=2025-07-01", exchange.Uri.Query);
            Assert.StartsWith("/secrets/" + Verifier.Value, exchange.Uri.AbsolutePath, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_adapter_offers_only_get_set_and_delete()
    {
        var declared = typeof(KeyVaultSecretStore)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal);
        Assert.Equal(["DeleteAsync", "GetAsync", "SetAsync", "ToString"], declared);
        Assert.Equal(
            ["IsVaultOrigin"],
            typeof(KeyVaultSecretStore).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly).Select(method => method.Name));
    }

    // --- Leaks -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task No_value_token_or_vault_answer_reaches_a_status_a_string_form_or_an_exception()
    {
        const string TokenSentinel = "SENTINEL-kv-access-token";
        const string ValueSentinel = "SENTINEL-kv-secret-value";
        const string AnswerSentinel = "SENTINEL-kv-provider-text";
        var (store, vault) = Leaking(TokenSentinel);
        var seen = new List<string> { store.ToString() };

        seen.Add((await store.SetAsync(Verifier, Encoding.ASCII.GetBytes(ValueSentinel), Cancellation)).ToString());
        var found = await store.GetAsync(Verifier, Cancellation);
        seen.Add(found.ToString());
        var exchanges = vault.Exchanges;

        // Every kind of failing answer carries the sentinels in its error code and message. Each operation runs on a vault
        // of its own with exactly the answers it consumes, so no scripted answer stays queued for a later step.
        var errorBody = "{\"error\":{\"code\":\"" + AnswerSentinel + "\",\"message\":\"" + AnswerSentinel + " " + ValueSentinel + "\"}}";
        foreach (var status in new[] { HttpStatusCode.InternalServerError, HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.Conflict })
        {
            var attempts = status == HttpStatusCode.InternalServerError ? KeyVaultSecretStore.MaxAttempts : 1;
            var (failingGet, failingGetVault) = Leaking(TokenSentinel);
            var (failingSet, failingSetVault) = Leaking(TokenSentinel);
            for (var answer = 0; answer < attempts; answer++)
            {
                failingGetVault.Then(FakeVault.Status(status, errorBody));
                failingSetVault.Then(FakeVault.Status(status, errorBody));
            }

            seen.Add((await failingGet.GetAsync(Verifier, Cancellation)).ToString());
            seen.Add((await failingSet.SetAsync(Verifier, Encoding.ASCII.GetBytes(ValueSentinel), Cancellation)).ToString());
            Assert.Equal(attempts, failingGetVault.Exchanges.Count);
            Assert.Equal(attempts, failingSetVault.Exchanges.Count);
        }

        var (odd, oddVault) = Leaking(TokenSentinel);
        oddVault.Then(FakeVault.Status(HttpStatusCode.OK, "{\"value\":\"" + ValueSentinel + "\",\"contentType\":\"" + AnswerSentinel + "\"}"));
        seen.Add((await odd.GetAsync(Verifier, Cancellation)).ToString());

        var (cancelling, cancellingVault) = Leaking(TokenSentinel);
        using (var caller = new CancellationTokenSource())
        {
            cancellingVault.Then((_, _, cancellationToken) =>
            {
                caller.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(FakeVault.Json(HttpStatusCode.OK, errorBody));
            });
            var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelling.GetAsync(Verifier, caller.Token).AsTask());
            seen.Add(cancelled.ToString());
        }

        var time = new SteppedTimeProvider();
        var refused = Assert.Throws<ArgumentException>(() =>
            new KeyVaultSecretStore(new Uri("https://" + ValueSentinel.ToLowerInvariant() + ".example.test/"), vault, new FixedTokenSource(TokenSentinel), new RecordingDelay(time), time));
        seen.Add(refused.ToString());

        foreach (var text in seen)
        {
            foreach (var sentinel in new[] { TokenSentinel, ValueSentinel, AnswerSentinel, ValueSentinel.ToLowerInvariant() })
            {
                Assert.DoesNotContain(sentinel, text, StringComparison.Ordinal);
            }
        }

        // Positive controls: the token and the value did travel to the vault, and the value came back.
        Assert.Contains(exchanges, exchange => exchange.Authorization == "Bearer " + TokenSentinel);
        Assert.Contains(exchanges, exchange => exchange.Body is { } body && Encoding.UTF8.GetString(body).Contains(Base64Url.EncodeToString(Encoding.ASCII.GetBytes(ValueSentinel)), StringComparison.Ordinal));
        Assert.Equal(Encoding.ASCII.GetBytes(ValueSentinel), ValueOf(found));
    }

    // --- Helpers ---------------------------------------------------------------------------------------------------

    private static IEnumerable<Func<KeyVaultSecretStore, CancellationToken, Task<MondaySecretStoreStatus>>> CancellableOperations =>
    [
        async (store, cancellationToken) => (await store.GetAsync(Verifier, cancellationToken)).Status,
        (store, cancellationToken) => store.SetAsync(Verifier, "v"u8.ToArray(), cancellationToken).AsTask(),
        (store, cancellationToken) => store.DeleteAsync(Verifier, cancellationToken).AsTask(),
    ];

    private IEnumerable<Func<KeyVaultSecretStore, Task<MondaySecretStoreStatus>>> Operations =>
        CancellableOperations.Select<Func<KeyVaultSecretStore, CancellationToken, Task<MondaySecretStoreStatus>>, Func<KeyVaultSecretStore, Task<MondaySecretStoreStatus>>>(
            operation => store => operation(store, Cancellation));

    private static (KeyVaultSecretStore Store, FakeVault Vault) Leaking(string token)
    {
        var time = new SteppedTimeProvider();
        var vault = new FakeVault(time);
        return (new KeyVaultSecretStore(FakeVault.Origin, vault, new FixedTokenSource(token), new RecordingDelay(time), time), vault);
    }

    private static (KeyVaultSecretStore Store, FakeVault Vault, SteppedTimeProvider Time, RecordingDelay Delay, FixedTokenSource Tokens) Compose()
    {
        var time = new SteppedTimeProvider();
        var vault = new FakeVault(time);
        var delay = new RecordingDelay(time);
        var tokens = new FixedTokenSource(Token);
        return (new KeyVaultSecretStore(FakeVault.Origin, vault, tokens, delay, time), vault, time, delay, tokens);
    }

    // The value of a found lookup, through the connector's own internal accessor.
    private static byte[] ValueOf(MondaySecretLookup lookup)
    {
        Assert.Equal(MondaySecretStoreStatus.Succeeded, lookup.Status);
        var field = typeof(MondaySecretLookup).GetField("_value", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The connector's lookup moved its value.");
        return ((byte[])field.GetValue(lookup)!).ToArray();
    }
}
