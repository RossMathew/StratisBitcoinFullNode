﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Stratis.Bitcoin.Features.Consensus;
using Stratis.Bitcoin.Features.Consensus.CoinViews;
using Stratis.Bitcoin.Features.Consensus.Interfaces;
using Stratis.Bitcoin.Features.MemoryPool;
using Stratis.Bitcoin.Features.MemoryPool.Interfaces;
using Stratis.Bitcoin.Features.Miner;
using Stratis.Bitcoin.Utilities;
using Stratis.SmartContracts;
using Stratis.SmartContracts.Backend;
using Stratis.SmartContracts.ContractValidation;
using Stratis.SmartContracts.State;

namespace Stratis.Bitcoin.Features.SmartContracts
{
    public class SmartContractBlockAssembler : PowBlockAssembler
    {
        private Money refundSender = 0;
        private List<TxOut> refundOutputs = new List<TxOut>();
        
        private readonly IContractStateRepository stateRoot;
        private readonly SmartContractDecompiler decompiler;
        private readonly SmartContractValidator validator;
        private readonly SmartContractGasInjector gasInjector;
        private readonly CoinView coinView;
        private uint160 coinbaseAddress;

        public SmartContractBlockAssembler(
            IConsensusLoop consensusLoop,
            Network network, 
            MempoolSchedulerLock mempoolLock, 
            ITxMempool mempool, 
            IDateTimeProvider dateTimeProvider, 
            ChainedBlock chainTip, 
            ILoggerFactory loggerFactory,
            IContractStateRepository stateRoot,
            SmartContractDecompiler decompiler,
            SmartContractValidator validator,
            SmartContractGasInjector gasInjector,
            CoinView coinView,
            AssemblerOptions options = null) : base(consensusLoop, network, mempoolLock, mempool, dateTimeProvider, chainTip, loggerFactory, options)
        {
            this.stateRoot = stateRoot;
            this.decompiler = decompiler;
            this.validator = validator;
            this.gasInjector = gasInjector;
            this.coinView = coinView;
        }

        // Copied from PowBlockAssembler, got rid of comments 
        public override BlockTemplate CreateNewBlock(Script scriptPubKeyIn, bool fMineWitnessTx = true)
        {
            this.pblock = this.pblocktemplate.Block; // Pointer for convenience.
            this.scriptPubKeyIn = scriptPubKeyIn;

            this.coinbaseAddress = new uint160(this.scriptPubKeyIn.GetDestinationPublicKeys().FirstOrDefault().Hash.ToBytes(), false); // TODO: This ugly af

            this.CreateCoinbase();
            this.ComputeBlockVersion();

            medianTimePast = Utils.DateTimeToUnixTime(this.ChainTip.GetMedianTimePast());
            this.lockTimeCutoff = PowConsensusValidator.StandardLocktimeVerifyFlags.HasFlag(Transaction.LockTimeFlags.MedianTimePast)
                ? medianTimePast
                : this.pblock.Header.Time;

            this.fIncludeWitness = false; //IsWitnessEnabled(pindexPrev, chainparams.GetConsensus()) && fMineWitnessTx;

            // add transactions from the mempool
            int nPackagesSelected;
            int nDescendantsUpdated;
            this.AddTransactions(out nPackagesSelected, out nDescendantsUpdated);

            lastBlockTx = this.blockTx;
            lastBlockSize = this.blockSize;
            lastBlockWeight = this.blockWeight;

            this.pblocktemplate.VTxFees[0] = -this.fees;
            this.coinbase.Outputs[0].Value = this.fees + this.consensusLoop.Validator.GetProofOfWorkReward(this.height);
            this.coinbase.Outputs.AddRange(this.refundOutputs);
            this.pblocktemplate.TotalFee = this.fees;

            int nSerializeSize = this.pblock.GetSerializedSize();

            this.UpdateHeaders();
            this.TestBlockValidity();

            return this.pblocktemplate;
        }

        protected override void AddToBlock(TxMempoolEntry iter)
        {
            // get contract txout - there is only allowed to be 1 per transaction 
            TxOut contractTxOut = iter.Transaction.Outputs.FirstOrDefault(txOut => txOut.ScriptPubKey.IsSmartContractExec);

            // boring transaction, handle normally
            if (contractTxOut == null)
            {
                base.AddToBlock(iter);
                return;
            }

            SmartContractTransaction scTransaction = new SmartContractTransaction(contractTxOut, iter.Transaction);
            scTransaction.Sender = GetSenderAddress(iter.Transaction, this.coinView, this.inBlock.Select(x => x.Transaction).ToList());
            AddContractCallToBlock(iter, scTransaction);
        }

        private void AddContractCallToBlock(TxMempoolEntry iter, SmartContractTransaction scTransaction)
        {
            IContractStateRepository track = this.stateRoot.StartTracking();
            ulong height = Convert.ToUInt64(this.height);// TODO: Optimise so this conversion isn't happening every time.
            ulong difficulty = 0; // TODO: Fix obviously this.consensusLoop.Chain.GetWorkRequired(this.network, this.height);
            SmartContractTransactionExecutor exec = new SmartContractTransactionExecutor(track, this.decompiler, this.validator, this.gasInjector, scTransaction, height, difficulty, this.coinbaseAddress);

            ulong gasToSpend = scTransaction.TotalGas;
            SmartContractExecutionResult result = exec.Execute();

            //Update state
            if (result.Revert)
                track.Rollback();
            else
                track.Commit();


            ulong toRefund = gasToSpend - result.GasUsed * scTransaction.GasPrice;
            ulong txFeeAndGas = iter.Fee - toRefund;

            // Add original transaction and fees to block
            this.pblock.AddTransaction(iter.Transaction);
            this.pblocktemplate.VTxFees.Add(txFeeAndGas);
            this.pblocktemplate.TxSigOpsCost.Add(iter.SigOpCost);
            if (this.needSizeAccounting)
                this.blockSize += iter.Transaction.GetSerializedSize();

            this.blockWeight += iter.TxWeight;
            this.blockTx++;
            this.blockSigOpsCost += iter.SigOpCost;
            this.fees += txFeeAndGas;
            this.inBlock.Add(iter);

            // Add internal transactions made during execution
            foreach(Transaction transaction in result.InternalTransactions)
            {
                this.pblock.AddTransaction(transaction);
                if (this.needSizeAccounting)
                    this.blockSize += transaction.GetSerializedSize();
                this.blockTx++;
            }

            // Setup refunds
            this.refundSender += toRefund;
            Script senderScript = new Script(
                OpcodeType.OP_DUP,
                OpcodeType.OP_HASH160,
                Op.GetPushOp(scTransaction.Sender.ToBytes()),
                OpcodeType.OP_EQUALVERIFY,
                OpcodeType.OP_CHECKSIG
            ); 
            this.refundOutputs.Add(new TxOut(toRefund, senderScript));
        }

        private uint160 GetSenderAddress(Transaction tx, CoinView coinView, IList<Transaction> blockTxs)
        {
            Script script = null;
            bool scriptFilled = false;

            if (blockTxs != null & blockTxs.Count > 0)
            {
                foreach(Transaction btx in blockTxs)
                {
                    if (btx.GetHash() == tx.Inputs[0].PrevOut.Hash)
                    {
                        script = btx.Outputs[tx.Inputs[0].PrevOut.N].ScriptPubKey;
                        scriptFilled = true;
                        break;
                    }
                }
            }
            if (!scriptFilled && coinView != null)
            {
                FetchCoinsResponse fetchCoinResult = coinView.FetchCoinsAsync(new uint256[] { tx.Inputs[0].PrevOut.Hash }).Result;
                script = fetchCoinResult.UnspentOutputs.FirstOrDefault().Outputs[0].ScriptPubKey;
                scriptFilled = true;
            }

            if (new PayToPubkeyTemplate().CheckScriptPubKey(script))
            {
                return new uint160(script.GetDestinationPublicKeys().FirstOrDefault().Hash.ToBytes(), false);
            }

            if (new PayToPubkeyHashTemplate().CheckScriptPubKey(script))
            {
                throw new NotImplementedException();
            }

            throw new NotImplementedException();   
        }

    }
}
