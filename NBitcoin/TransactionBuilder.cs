﻿using NBitcoin.OpenAsset;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Builder = System.Func<NBitcoin.TransactionBuildingContext, NBitcoin.Money>;

namespace NBitcoin
{
	public interface ICoinSelector
	{
		IEnumerable<ICoin> Select(IEnumerable<ICoin> coins, Money target);
	}

	/// <summary>
	/// Algorithm implemented by bitcoin core https://github.com/bitcoin/bitcoin/blob/master/src/wallet.cpp#L1276
	/// Minimize the change
	/// </summary>
	public class DefaultCoinSelector : ICoinSelector
	{
		public DefaultCoinSelector()
		{

		}
		Random _Rand = new Random();
		public DefaultCoinSelector(int seed)
		{
			_Rand = new Random(seed);
		}
		#region ICoinSelector Members

		public IEnumerable<ICoin> Select(IEnumerable<ICoin> coins, Money target)
		{
			var targetCoin = coins
							.FirstOrDefault(c => c.Amount == target);
			//If any of your UTXO² matches the Target¹ it will be used.
			if(targetCoin != null)
				return new[] { targetCoin };

			var orderedCoins = coins.OrderBy(s => s.Amount).ToArray();
			List<ICoin> result = new List<ICoin>();
			Money total = Money.Zero;

			foreach(var coin in orderedCoins)
			{
				if(coin.Amount < target && total < target)
				{
					total += coin.Amount;
					result.Add(coin);
					//If the "sum of all your UTXO smaller than the Target" happens to match the Target, they will be used. (This is the case if you sweep a complete wallet.)
					if(total == target)
						return result;

				}
				else
				{
					if(total < target && coin.Amount > target)
					{
						//If the "sum of all your UTXO smaller than the Target" doesn't surpass the target, the smallest UTXO greater than your Target will be used.
						return new[] { coin };
					}
					else
					{
						//						Else Bitcoin Core does 1000 rounds of randomly combining unspent transaction outputs until their sum is greater than or equal to the Target. If it happens to find an exact match, it stops early and uses that.
						//Otherwise it finally settles for the minimum of
						//the smallest UTXO greater than the Target
						//the smallest combination of UTXO it discovered in Step 4.
						var allCoins = orderedCoins.ToArray();
						Money minTotal = null;
						List<ICoin> minSelection = null;
						for(int _ = 0 ; _ < 1000 ; _++)
						{
							var selection = new List<ICoin>();
							Shuffle(allCoins, _Rand);
							total = Money.Zero;
							for(int i = 0 ; i < allCoins.Length ; i++)
							{
								selection.Add(allCoins[i]);
								total += allCoins[i].Amount;
								if(total == target)
									return selection;
								if(total > target)
									break;
							}
							if(total < target)
							{
								return null;
							}
							if(minTotal == null || total < minTotal)
							{
								minTotal = total;
								minSelection = selection;
							}
						}
					}
				}
			}
			if(total < target)
				return null;
			return result;
		}

		internal static void Shuffle<T>(T[] list, Random random)
		{
			int n = list.Length;
			while(n > 1)
			{
				n--;
				int k = random.Next(n + 1);
				T value = list[k];
				list[k] = list[n];
				list[n] = value;
			}
		}
		internal static void Shuffle<T>(List<T> list, Random random)
		{
			int n = list.Count;
			while(n > 1)
			{
				n--;
				int k = random.Next(n + 1);
				T value = list[k];
				list[k] = list[n];
				list[n] = value;
			}
		}


		#endregion
	}

	public class NotEnoughFundsException : Exception
	{
		public NotEnoughFundsException()
		{
		}
		public NotEnoughFundsException(string message)
			: base(message)
		{
		}
		public NotEnoughFundsException(string message, Exception inner)
			: base(message, inner)
		{
		}
	}

	class TransactionBuildingContext
	{
		public TransactionBuildingContext(TransactionBuilder builder)
		{
			Builder = builder;
			Transaction = new Transaction();
		}
		public TransactionBuilder Builder
		{
			get;
			set;
		}
		public Transaction Transaction
		{
			get;
			set;
		}

		ColorMarker _Marker;
		TxOut _MarkerOutput;
		public ColorMarker GetColorMarker(bool issuance)
		{
			if(_Marker == null)
				_Marker = new ColorMarker();
			if(!issuance && _MarkerOutput == null)
			{
				_MarkerOutput = Transaction.AddOutput(new TxOut());
				_MarkerOutput.Value = Money.Zero;
			}
			return _Marker;
		}

		public void Finish()
		{
			if(_Marker != null)
			{
				if(_MarkerOutput == null)
				{
					_MarkerOutput = Transaction.AddOutput(new TxOut());
					_MarkerOutput.Value = Money.Zero;
				}
				_MarkerOutput.ScriptPubKey = _Marker.GetScript();
			}
		}

		public IssuanceCoin IssuanceCoin
		{
			get;
			set;
		}
	}
	public class TransactionBuilder
	{
		internal class BuilderGroup
		{
			TransactionBuilder _Parent;
			public BuilderGroup(TransactionBuilder parent)
			{
				_Parent = parent;
			}
			internal List<Builder> Builders = new List<Builder>();
			internal List<ICoin> Coins = new List<ICoin>();
			internal List<Builder> IssuanceBuilders = new List<Builder>();
			internal Dictionary<ScriptId, List<Builder>> BuildersByAsset = new Dictionary<ScriptId, List<Builder>>();
			internal Script ChangeScript;
			internal void Shuffle()
			{
				Shuffle(Builders);
				foreach(var builders in BuildersByAsset)
					Shuffle(builders.Value);
				Shuffle(IssuanceBuilders);
			}
			private void Shuffle(List<Builder> builders)
			{
				DefaultCoinSelector.Shuffle(builders, _Parent._Rand);
			}
		}

		List<BuilderGroup> _BuilderGroups = new List<BuilderGroup>();
		BuilderGroup _CurrrentGroup = null;
		internal BuilderGroup CurrentGroup
		{
			get
			{
				if(_CurrrentGroup == null)
				{
					_CurrrentGroup = new BuilderGroup(this);
					_BuilderGroups.Add(_CurrrentGroup);
				}
				return _CurrrentGroup;
			}
		}

		delegate TxOut SetChangeDelegate(TransactionBuildingContext ctx, Money change, Script changeScript);
		public TransactionBuilder()
		{
			Fees = Money.Zero;
			_Rand = new Random();
			CoinSelector = new DefaultCoinSelector();
			ColoredDust = Money.Parse("0.000006");
		}
		internal Random _Rand;
		public TransactionBuilder(int seed)
		{
			Fees = Money.Zero;
			_Rand = new Random(seed);
			CoinSelector = new DefaultCoinSelector(seed);
			ColoredDust = Money.Parse("0.000006");
		}
		public Money Fees
		{
			get;
			set;
		}

		public ICoinSelector CoinSelector
		{
			get;
			set;
		}

		List<Key> _Keys = new List<Key>();

		public TransactionBuilder AddKeys(params Key[] keys)
		{
			_Keys.AddRange(keys);
			return this;
		}

		public TransactionBuilder AddCoins(params ICoin[] coins)
		{
			CurrentGroup.Coins.AddRange(coins);
			return this;
		}
		public TransactionBuilder Send(BitcoinAddress destination, Money amount)
		{
			return Send(destination.ID, amount);
		}

		public TransactionBuilder Send(TxDestination id, Money amount)
		{
			return Send(id.CreateScriptPubKey(), amount);
		}
		public TransactionBuilder Send(Script scriptPubKey, Money amount)
		{
			CurrentGroup.Builders.Add(ctx =>
			{
				ctx.Transaction.Outputs.Add(new TxOut(amount, scriptPubKey));
				return amount;
			});
			return this;
		}

		public TransactionBuilder SendAsset(BitcoinAddress destination, Asset asset)
		{
			return SendAsset(destination.ID, asset);
		}

		public TransactionBuilder SendAsset(PubKey pubKey, Asset asset)
		{
			return SendAsset(pubKey.PaymentScript, asset);
		}

		public TransactionBuilder SendAsset(TxDestination id, Asset asset)
		{
			return SendAsset(id.CreateScriptPubKey(), asset);
		}

		public TransactionBuilder Shuffle()
		{
			DefaultCoinSelector.Shuffle(_BuilderGroups, _Rand);
			foreach(var group in _BuilderGroups)
				group.Shuffle();
			return this;
		}

		public TransactionBuilder SendAsset(Script scriptPubKey, Asset asset)
		{
			AssertOpReturn("Colored Coin");
			var builders = CurrentGroup.BuildersByAsset.TryGet(asset.Id);
			if(builders == null)
			{
				builders = new List<Builder>();
				CurrentGroup.BuildersByAsset.Add(asset.Id, builders);
			}
			builders.Add(ctx =>
			{
				var marker = ctx.GetColorMarker(false);
				ctx.Transaction.AddOutput(new TxOut(ColoredDust, scriptPubKey));
				marker.SetQuantity(ctx.Transaction.Outputs.Count - 2, asset.Quantity);
				return asset.Quantity;
			});
			return this;
		}

		public Money ColoredDust
		{
			get;
			set;
		}


		string _OpReturnUser;
		private void AssertOpReturn(string name)
		{
			if(_OpReturnUser == null)
			{
				_OpReturnUser = name;
			}
			else
			{
				if(_OpReturnUser != name)
					throw new InvalidOperationException("Op return already used for " + _OpReturnUser);
			}
		}

		public TransactionBuilder Send(BitcoinStealthAddress address, Money money, Key ephemKey = null)
		{
			if(_OpReturnUser == null)
				_OpReturnUser = "Stealth Payment";
			else
				throw new InvalidOperationException("Op return already used for " + _OpReturnUser);

			CurrentGroup.Builders.Add(ctx =>
			{
				var payment = address.CreatePayment(ephemKey);
				payment.AddToTransaction(ctx.Transaction, money);
				return money;
			});
			return this;
		}

		public TransactionBuilder IssueAsset(BitcoinAddress address, Asset asset)
		{
			return IssueAsset(address.ID, asset);
		}

		public TransactionBuilder IssueAsset(TxDestination destination, Asset asset)
		{
			return IssueAsset(destination.CreateScriptPubKey(), asset);
		}
		public TransactionBuilder IssueAsset(PubKey destination, Asset asset)
		{
			return IssueAsset(destination.PaymentScript, asset);
		}

		ScriptId _IssuedAsset;

		private TransactionBuilder IssueAsset(Script scriptPubKey, Asset asset)
		{
			AssertOpReturn("Colored Coin");
			if(_IssuedAsset == null)
				_IssuedAsset = asset.Id;
			else if(_IssuedAsset != asset.Id)
				throw new InvalidOperationException("You can issue only one asset type in a transaction");

			CurrentGroup.IssuanceBuilders.Add(ctx =>
			{
				var marker = ctx.GetColorMarker(true);
				if(ctx.IssuanceCoin == null)
				{
					var issuance = CurrentGroup.Coins.OfType<IssuanceCoin>().Where(i => i.AssetId == asset.Id).FirstOrDefault();
					if(issuance == null)
						throw new InvalidOperationException("No issuance coin for emitting asset found");
					ctx.IssuanceCoin = issuance;
					ctx.Transaction.Inputs.Insert(0, new TxIn(issuance.Outpoint));
				}

				ctx.Transaction.AddOutput(ColoredDust, scriptPubKey);
				marker.SetQuantity(ctx.Transaction.Outputs.Count - 1, asset.Quantity);
				return asset.Quantity;
			});
			return this;
		}

		public TransactionBuilder SetFees(Money fees)
		{
			if(fees == null)
				throw new ArgumentNullException("fees");
			Fees = fees;
			return this;
		}

		public TransactionBuilder SetChange(BitcoinAddress destination)
		{
			return SetChange(destination.ID);
		}

		public TransactionBuilder SetChange(TxDestination destination)
		{
			return SetChange(destination.CreateScriptPubKey());
		}

		public TransactionBuilder SetChange(PubKey pubKey)
		{
			return SetChange(pubKey.PaymentScript);
		}

		public TransactionBuilder SetChange(Script scriptPubKey)
		{
			CurrentGroup.ChangeScript = scriptPubKey;
			return this;
		}

		public TransactionBuilder SetCoinSelector(ICoinSelector selector)
		{
			if(selector == null)
				throw new ArgumentNullException("selector");
			CoinSelector = selector;
			return this;
		}

		private TxOut AddColoredChange(TransactionBuildingContext ctx, Money change, Script changeScript)
		{
			var txout = ctx.Transaction.AddOutput(new TxOut(ColoredDust, changeScript));
			ctx.GetColorMarker(false).SetQuantity(ctx.Transaction.Outputs.Count - 2, change);
			return txout;
		}

		private TxOut AddChange(TransactionBuildingContext ctx, Money change, Script changeScript)
		{
			return ctx.Transaction.AddOutput(new TxOut(change, changeScript));
		}

		public Transaction BuildTransaction(bool sign)
		{
			TransactionBuildingContext ctx = new TransactionBuildingContext(this);
			foreach(var group in _BuilderGroups)
			{
				foreach(var builder in group.IssuanceBuilders)
					builder(ctx);

				var buildersByAsset = group.BuildersByAsset.ToList();
				foreach(var builders in buildersByAsset)
				{
					var coins = group.Coins.OfType<ColoredCoin>().Where(c => c.Asset.Id == builders.Key).OfType<ICoin>();
					BuildTransaction(ctx, builders.Value, coins, Money.Zero, group.ChangeScript, AddColoredChange);
				}

				BuildTransaction(ctx, group.Builders, group.Coins.OfType<Coin>(), Fees, group.ChangeScript, AddChange);
			}
			ctx.Finish();

			if(sign)
			{
				SignTransactionInPlace(ctx.Transaction);
			}
			return ctx.Transaction;
		}

		private void BuildTransaction(
			TransactionBuildingContext ctx,
			List<Builder> builders,
			IEnumerable<ICoin> coins,
			Money fees,
			Script changeScript,
			SetChangeDelegate setChange)
		{
			builders = builders.ToList();

			var amount = builders.Select(b => b(ctx)).Sum();
			var target = amount + fees;
			var selection = CoinSelector.Select(coins, target);
			if(selection == null)
				throw new NotEnoughFundsException("Not enough fund to cover the target");
			var total = selection.Select(s => s.Amount).Sum();
			if(total < target)
				throw new NotEnoughFundsException("Not enough fund to cover the target");
			if(total != target)
			{
				if(changeScript == null)
					throw new InvalidOperationException("A change address should be specified");
				setChange(ctx, total - target, changeScript);
			}
			foreach(var coin in selection)
			{
				ctx.Transaction.AddInput(new TxIn(coin.Outpoint));
			}
		}
		public Transaction SignTransaction(Transaction transaction)
		{
			var tx = transaction.Clone();
			SignTransactionInPlace(tx);
			return tx;
		}
		public void SignTransactionInPlace(Transaction transaction)
		{
			for(int i = 0 ; i < transaction.Inputs.Count ; i++)
			{
				var txIn = transaction.Inputs[i];
				var coin = FindCoin(txIn.PrevOut);
				if(coin != null)
				{
					Sign(transaction, txIn, coin, i);
				}
			}
		}

		public bool Verify(Transaction tx)
		{
			for(int i = 0 ; i < tx.Inputs.Count ; i++)
			{
				var txIn = tx.Inputs[i];
				var coin = FindCoin(txIn.PrevOut);
				if(coin == null)
					throw new KeyNotFoundException("Impossible to find the scriptPubKey of outpoint " + txIn.PrevOut);
				if(!Script.VerifyScript(txIn.ScriptSig, coin.ScriptPubKey, tx, i))
					return false;
			}
			return true;
		}

		private ICoin FindCoin(OutPoint outPoint)
		{
			return _BuilderGroups.SelectMany(c => c.Coins).FirstOrDefault(c => c.Outpoint == outPoint);
		}

		readonly static PayToScriptHashTemplate payToScriptHash = new PayToScriptHashTemplate();
		readonly static PayToPubkeyHashTemplate payToPubKeyHash = new PayToPubkeyHashTemplate();
		readonly static PayToPubkeyTemplate payToPubKey = new PayToPubkeyTemplate();
		readonly static PayToMultiSigTemplate payToMultiSig = new PayToMultiSigTemplate();

		private void Sign(Transaction tx, TxIn input, ICoin coin, int n)
		{
			if(coin is ColoredCoin)
				coin = ((ColoredCoin)coin).Bearer;
			if(payToScriptHash.CheckScriptPubKey(coin.ScriptPubKey))
			{
				var scriptCoin = coin as ScriptCoin;
				if(scriptCoin == null)
				{
					//Try to extract redeem from this transaction
					var p2shParams = payToScriptHash.ExtractScriptSigParameters(input.ScriptSig);
					if(p2shParams == null)
						throw new InvalidOperationException("A coin with a P2SH scriptPubKey was detected, however this coin is not a ScriptCoin");
					else
					{
						scriptCoin = new ScriptCoin(coin.Outpoint, ((Coin)coin).TxOut, p2shParams.RedeemScript);
					}
				}

				var original = input.ScriptSig;
				input.ScriptSig = CreateScriptSig(tx, input, coin, n, scriptCoin.Redeem);
				if(original != input.ScriptSig)
				{
					var ops = input.ScriptSig.ToOps().ToList();
					ops.Add(Op.GetPushOp(scriptCoin.Redeem.ToRawScript(true)));
					input.ScriptSig = new Script(ops.ToArray());
				}
			}
			else if(coin is StealthCoin)
			{
				var stealthCoin = (StealthCoin)coin;
				List<Key> tempKeys = new List<Key>();
				var scanKey = FindKey(stealthCoin.Address.ScanPubKey);
				if(scanKey == null)
					throw new KeyNotFoundException("Scan key for decrypting StealthCoin not found");
				foreach(var key in stealthCoin.Address.SpendPubKeys.Select(p => FindKey(p)).Where(p => p != null))
				{
					tempKeys.Add(key.Uncover(scanKey, stealthCoin.StealthMetadata.EphemKey));
				}
				_Keys.AddRange(tempKeys);
				try
				{
					input.ScriptSig = CreateScriptSig(tx, input, coin, n, coin.ScriptPubKey);

				}
				finally
				{
					foreach(var tempKey in tempKeys)
					{
						_Keys.Remove(tempKey);
					}
				}
			}
			else
			{
				input.ScriptSig = CreateScriptSig(tx, input, coin, n, coin.ScriptPubKey);
			}
		}


		private Script CreateScriptSig(Transaction tx, TxIn input, ICoin coin, int n, Script scriptPubKey)
		{
			var originalScriptSig = input.ScriptSig;
			input.ScriptSig = scriptPubKey;

			var pubKeyHashParams = payToPubKeyHash.ExtractScriptPubKeyParameters(scriptPubKey);
			if(pubKeyHashParams != null)
			{
				var key = FindKey(pubKeyHashParams);
				if(key == null)
					return originalScriptSig;
				var hash = input.ScriptSig.SignatureHash(tx, n, SigHash.All);
				var sig = key.Sign(hash);
				return payToPubKeyHash.GenerateScriptSig(new TransactionSignature(sig, SigHash.All), key.PubKey);
			}

			var multiSigParams = payToMultiSig.ExtractScriptPubKeyParameters(scriptPubKey);
			if(multiSigParams != null)
			{
				var alreadySigned = payToMultiSig.ExtractScriptSigParameters(originalScriptSig);
				if(alreadySigned == null && !Script.IsNullOrEmpty(originalScriptSig)) //Maybe a P2SH
				{
					var ops = originalScriptSig.ToOps().ToList();
					ops.RemoveAt(ops.Count - 1);
					alreadySigned = payToMultiSig.ExtractScriptSigParameters(new Script(ops.ToArray()));
				}
				List<TransactionSignature> signatures = new List<TransactionSignature>();
				if(alreadySigned != null)
				{
					signatures.AddRange(alreadySigned);
				}
				var keys =
					multiSigParams
					.PubKeys
					.Select(p => FindKey(p))
					.ToArray();

				int sigCount = signatures.Where(s => s != TransactionSignature.Empty).Count();
				for(int i = 0 ; i < keys.Length ; i++)
				{
					if(sigCount == multiSigParams.SignatureCount)
						break;

					if(i >= signatures.Count)
					{
						signatures.Add(TransactionSignature.Empty);
					}
					if(keys[i] != null)
					{
						var hash = input.ScriptSig.SignatureHash(tx, n, SigHash.All);
						var sig = keys[i].Sign(hash);
						signatures[i] = new TransactionSignature(sig, SigHash.All);
						sigCount++;
					}
				}

				if(sigCount == multiSigParams.SignatureCount)
				{
					signatures = signatures.Where(s => s != TransactionSignature.Empty).ToList();
				}

				return payToMultiSig.GenerateScriptSig(
					signatures.ToArray());
			}

			var pubKeyParams = payToPubKey.ExtractScriptPubKeyParameters(scriptPubKey);
			if(pubKeyParams != null)
			{
				var key = FindKey(pubKeyParams);
				if(key == null)
					return originalScriptSig;
				var hash = input.ScriptSig.SignatureHash(tx, n, SigHash.All);
				var sig = key.Sign(hash);
				return payToPubKey.GenerateScriptSig(new TransactionSignature(sig, SigHash.All));
			}

			throw new NotSupportedException("Unsupported scriptPubKey");
		}


		private Key FindKey(TxDestination id)
		{
			return _Keys.FirstOrDefault(k => k.PubKey.ID == id);
		}

		private Key FindKey(PubKey pubKeyParams)
		{
			return _Keys.FirstOrDefault(k => k.PubKey == pubKeyParams);
		}

		public TransactionBuilder Flush()
		{
			_CurrrentGroup = null;
			return this;
		}

		public TransactionBuilder Send(PubKey pubKey, Money amount)
		{
			return Send(pubKey.PaymentScript, amount);
		}
	}
}
