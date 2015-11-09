SELECT [champ2Id], [champ1Win] into temp FROM [dbo].[OneForAllOct2015]
UPDATE temp SET champ1Win = 'x' WHERE champ1Win = 'True';
UPDATE temp SET champ1Win = 'True' WHERE champ1Win = 'False';
UPDATE temp SET champ1Win = 'False' WHERE champ1Win = 'x'; 

SELECT champ1Id AS champ, champ1Win AS win INTO AllChampEntries FROM
(SELECT [champ1Id], [champ1Win] FROM [dbo].[OneForAllOct2015]
UNION all
SELECT [champ2Id], [champ1Win] FROM [dbo].[temp]) AS allChamps

/** Popularity rankings */
SELECT champ, COUNT(*)/CAST(205612 AS float) FROM
AllChampEntries
GROUP BY champ
ORDER BY COUNT(*) DESC

SELECT totalTemp.champ, totalTemp.total, winTemp.wins INTO OneForAllWins FROM
(SELECT champ, COUNT(*) as total FROM AllChampEntries
GROUP BY champ) totalTemp
JOIN
(SELECT champ, COUNT(*) as wins FROM AllChampEntries WHERE win = 'True'
GROUP BY champ) winTemp
ON (totalTemp.champ = winTemp.champ) 

/** Win rate rankings */
SELECT champ, wins * 1.0/ total as winRate FROM OneForAllWins WHERE total > 100
ORDER BY winRate DESC