# Function-level retrieval: predictions, written before any run

Question: does attaching a member's leading comments and attributes to its segment (`CodeChunker`, `language-aware`) change
retrieval of the member a developer's question is about, and does it matter more at small chunk sizes?

Written 2026-11-11 before `functionbench.py` was run on any arm. The chunker change is a prototype in the working tree; the
labels (which member answers each of the 52 questions in `queries-code.json`) were made by a reviewer who saw no result.

## Design

- Corpus: `src/` at tree `f239ba4`, the same 143 text files for every arm, chunked by the production `CodeChunker`
  (`language-aware`) at 256/32, 512/64 and 2065/258 tokens/overlap. OLD is the chunker of `f239ba4`; NEW is the prototype.
- Models: `embeddinggemma:latest` and `embeddinggemma-2:q8_0` (the local alias), each with its own framing as in `modelbench.py`.
- Queries: the 52 in `queries-code.json`. A hit is a chunk that contains the signature line of a labelled member of the expected
  file; the score is the reciprocal rank of the first hit in the top 10, dense only. File-level MRR is reported beside it.
- Paired 95% bootstrap interval over the 52 queries for NEW minus OLD.

## Predictions

| id | prediction | probability |
|---|---|---|
| P1 | At 256/32, NEW beats OLD in function-level MRR for both models, by at least 0.03. | 65% |
| P2 | At 512/64, NEW minus OLD is between 0 and +0.03 for both models. | 55% |
| P3 | At 2065/258, NEW and OLD differ by at most 0.02 for both models. | 80% |
| P4 | At 256/32 the paired interval excludes zero for at least one model. | 35% |
| P5 | File-level MRR differs by less than 0.02 between NEW and OLD at every size and model. | 80% |
| P6 | For each model and chunker, the best function-level MRR is at 256/32 or 512/64, not at 2065/258. | 70% |

## Decision rule

The chunker change ships only if, at 256/32 or 512/64, NEW minus OLD is at least +0.02 for both models and the interval
excludes zero for at least one of them. Otherwise it stays a documented finding (the share of documented members whose
comment sits in the same chunk as their signature: 46.8% at 256/32 on `f239ba4`) and the chunker is left as it is. Shipping
it also changes the chunking of every code file, so it needs its own decision about re-chunking existing corpora.
